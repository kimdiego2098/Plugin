﻿//------------------------------------------------------------------------------
//  此代码版权声明为全文件覆盖，如有原作者特别声明，会在下方手动补充
//  此代码版权（除特别声明外的代码）归作者本人Diego所有
//  源代码使用协议遵循本仓库的开源协议及附加协议
//  Gitee源代码仓库：https://gitee.com/diego2098/ThingsGateway
//  Github源代码仓库：https://github.com/kimdiego2098/ThingsGateway
//  使用文档：https://thingsgateway.cn/
//  QQ群：605534569
//------------------------------------------------------------------------------

using Newtonsoft.Json.Linq;

using Opc.Ua;

using ThingsGateway.Core.Json.Extension;
using ThingsGateway.Foundation.Extension.Generic;
using ThingsGateway.Foundation.OpcUa;
using ThingsGateway.Gateway.Application;
using ThingsGateway.NewLife.Threading;

using TouchSocket.Core;

namespace ThingsGateway.Plugin.OpcUa;

/// <summary>
/// <inheritdoc/>
/// </summary>
public class OpcUaMaster : CollectBase
{
    private readonly OpcUaMasterProperty _driverProperties = new();

    private ThingsGateway.Foundation.OpcUa.OpcUaMaster _plc;

    private CancellationToken _token;

    private volatile bool connectFirstFail;
    private volatile bool connectFirstFailLoged;
    private volatile bool success = true;

    /// <inheritdoc/>
    public override CollectPropertyBase CollectProperties => _driverProperties;

    /// <inheritdoc/>
    public override Type DriverDebugUIType => typeof(ThingsGateway.Debug.OpcUaMaster);

    public override Type DriverPropertyUIType => typeof(OpcUaMasterPropertyRazor);

    public override Type DriverUIType => null;

    /// <inheritdoc/>
    public override IProtocol Protocol => null;

    /// <inheritdoc/>
    protected override void Init(IChannel? channel = null)
    {
        //载入配置
        OpcUaProperty config = new()
        {
            OpcUrl = _driverProperties.OpcUrl,
            UpdateRate = _driverProperties.UpdateRate,
            DeadBand = _driverProperties.DeadBand,
            GroupSize = _driverProperties.GroupSize,
            KeepAliveInterval = _driverProperties.KeepAliveInterval,
            UseSecurity = _driverProperties.UseSecurity,
            ActiveSubscribe = _driverProperties.ActiveSubscribe,
            UserName = _driverProperties.UserName,
            Password = _driverProperties.Password,
            CheckDomain = _driverProperties.CheckDomain,
            LoadType = _driverProperties.LoadType,
            AutoAcceptUntrustedCertificates=_driverProperties.AutoAcceptUntrustedCertificates,
        };
        if (_plc == null)
        {
            _plc = new();
            _plc.LogEvent += _plc_LogEvent;
            _plc.DataChangedHandler += DataChangedHandler;
        }
        _plc.OpcUaProperty = config;
    }

    /// <inheritdoc/>
    public override bool IsConnected() => _plc?.Connected == true;

    public override string ToString()
    {
        return $"{_driverProperties.OpcUrl}";
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_plc != null)
        {
            _plc.DataChangedHandler -= DataChangedHandler;
            _plc.LogEvent -= _plc_LogEvent;

            _plc.Disconnect();
            _plc.SafeDispose();
        }
        base.Dispose(disposing);
    }

    protected override string GetAddressDescription()
    {
        return _plc?.GetAddressDescription();
    }

    protected override async Task ProtectedBeforStartAsync(CancellationToken cancellationToken)
    {
        _token = cancellationToken;
        try
        {
            await _plc.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            LogMessage?.LogWarning(ex, "Connect Fail");
            connectFirstFail = true;
        }
        await base.ProtectedBeforStartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask ProtectedExecuteAsync(CancellationToken cancellationToken)
    {
        if (connectFirstFail && !IsConnected())
        {
            try
            {
                await _plc.ConnectAsync(cancellationToken).ConfigureAwait(false);
                connectFirstFail = false;
            }
            catch (Exception ex)
            {
                if (!connectFirstFailLoged)
                    LogMessage?.LogWarning(ex, "Connect Fail");

                connectFirstFailLoged = true;
                CurrentDevice.SetDeviceStatus(TimerX.Now, 999, ex.Message);
                await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            connectFirstFail = false;
        }
        if (_driverProperties.ActiveSubscribe)
        {
            ScriptVariableRun(cancellationToken);

            //获取设备连接状态
            if (IsConnected())
            {
                //更新设备活动时间
                CurrentDevice.SetDeviceStatus(TimerX.Now, 0);
            }
            else
            {
                CurrentDevice.SetDeviceStatus(TimerX.Now, 999);
            }
        }
        else
        {
            await base.ProtectedExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    protected override List<VariableSourceRead> ProtectedLoadSourceRead(List<VariableRunTime> deviceVariables)
    {
        if (deviceVariables.Count > 0)
        {
            var dataLists = deviceVariables.ChunkBetter(_driverProperties.GroupSize);
            _plc.Variables = new();
            _plc.Variables.AddRange(dataLists.Select(a => a.Where(a => !a.RegisterAddress.IsNullOrEmpty()).Select(a => a.RegisterAddress!).ToList()).ToList());
            var dataResult = new List<VariableSourceRead>();
            foreach (var variable in dataLists)
            {
                var sourVars = new VariableSourceRead()
                {
                    TimeTick = new(_driverProperties.UpdateRate.ToString()),
                    RegisterAddress = "",
                };
                foreach (var item in variable)
                {
                    sourVars.AddVariable(item);
                }
                dataResult.Add(sourVars);
            }

            return dataResult;
        }
        else
        {
            return new();
        }
    }

    /// <inheritdoc/>
    protected override async ValueTask<OperResult<byte[]>> ReadSourceAsync(VariableSourceRead deviceVariableSourceRead, CancellationToken cancellationToken)
    {
        DateTime time = DateTime.Now;
        var addresss = deviceVariableSourceRead.VariableRunTimes.Where(a => !a.RegisterAddress.IsNullOrEmpty()).Select(a => a.RegisterAddress!).ToArray();
        try
        {
            await WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = await _plc.ReadJTokenValueAsync(addresss, cancellationToken).ConfigureAwait(false);
            foreach (var data in result)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    var data1 = deviceVariableSourceRead.VariableRunTimes.Where(a => a.RegisterAddress == data.Item1);

                    foreach (var item in data1)
                    {
                        object value;
                        if (data.Item3 is JValue jValue)
                        {
                            value = jValue.Value;
                        }
                        else
                        {
                            value = data.Item3;
                        }

                        var isGood = StatusCode.IsGood(data.Item2.StatusCode);
                        if (_driverProperties.SourceTimestampEnable)
                        {
                            time = data.Item2.SourceTimestamp.ToLocalTime();
                        }
                        if (isGood)
                        {
                            item.SetValue(value, time);
                        }
                        else
                        {
                            item.SetValue(null, time, false);
                            item.VariableSource.LastErrorMessage = data.Item2.StatusCode.ToString();
                        }
                    }
                    LogMessage.Trace($"{ToString()} Change:{Environment.NewLine}{data.Item1} : {data.Item3}");
                }
            }
            if (result.Any(a => StatusCode.IsBad(a.Item2.StatusCode)))
            {
                return new OperResult<byte[]>($"OPC quality bad");
            }
            else
            {
                return OperResult.CreateSuccessResult<byte[]>(null);
            }
        }
        catch (Exception ex)
        {
            return new OperResult<byte[]>($"ReadSourceAsync {addresss.ToJsonNetString()}：{Environment.NewLine}{ex}");
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <inheritdoc/>
    protected override async ValueTask<Dictionary<string, OperResult>> WriteValuesAsync(Dictionary<VariableRunTime, JToken> writeInfoLists, CancellationToken cancellationToken)
    {
        try
        {
            await WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = await _plc.WriteNodeAsync(writeInfoLists.ToDictionary(a => a.Key.RegisterAddress!, a => a.Value), cancellationToken).ConfigureAwait(false);
            return result.ToDictionary<KeyValuePair<string, Tuple<bool, string>>, string, OperResult>(a =>
            {
                return writeInfoLists.Keys.FirstOrDefault(b => b.RegisterAddress == a.Key)?.Name!;
            }
            , a =>
            {
                if (!a.Value.Item1)
                    return new OperResult(a.Value.Item2);
                else
                    return OperResult.Success;
            })!;
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private void _plc_LogEvent(byte level, object sender, string message, Exception ex)
    {
        LogMessage?.Log((LogLevel)level, sender, message, ex);
    }

    private void DataChangedHandler((VariableNode variableNode, DataValue dataValue, JToken jToken) data)
    {
        DateTime time = DateTime.Now;
        try
        {
            if (!CurrentDevice.KeepRun)
                return;
            if (_token.IsCancellationRequested)
                return;

            LogMessage.Trace($"{ToString()} Change: {Environment.NewLine} {data.variableNode.NodeId} : {data.jToken?.ToString()}");

            if (!CurrentDevice.KeepRun)
            {
                return;
            }
            //尝试固定点位的数据类型
            var type = TypeInfo.GetSystemType(TypeInfo.GetBuiltInType(data.variableNode.DataType, _plc.Session.SystemContext.TypeTable), data.variableNode.ValueRank);

            var itemReads = CurrentDevice.VariableRunTimes.Select(a => a.Value).Where(it => it.RegisterAddress == data.variableNode.NodeId);

            object value;
            if (data.jToken is JValue jValue)
            {
                value = jValue.Value;
            }
            else
            {
                value = data.jToken;
            }
            var isGood = StatusCode.IsGood(data.dataValue.StatusCode);
            if (_driverProperties.SourceTimestampEnable)
            {
                time = data.dataValue.SourceTimestamp.ToLocalTime();
            }
            foreach (var item in itemReads)
            {
                if (!CurrentDevice.KeepRun)
                    return;
                if (_token.IsCancellationRequested)
                    return;
                if (item.DataType == DataTypeEnum.Object)
                    if (type.Namespace.StartsWith("System"))
                    {
                        var enumValues = Enum.GetValues<DataTypeEnum>();
                        var stringList = enumValues.Select(e => e.ToString());
                        if (stringList.Contains(type.Name))
                            try { item.DataType = Enum.Parse<DataTypeEnum>(type.Name); } catch { }
                    }
                if (isGood)
                {
                    item.SetValue(value, time);
                }
                else
                {
                    item.SetValue(null, time, false);
                    item.VariableSource.LastErrorMessage = data.Item2.StatusCode.ToString();
                }
            }
            success = true;
        }
        catch (Exception ex)
        {
            if (success)
                LogMessage?.LogWarning(ex);
            success = false;
        }
    }
}
