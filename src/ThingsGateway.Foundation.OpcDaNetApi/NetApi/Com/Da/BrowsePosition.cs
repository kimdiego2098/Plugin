﻿

using Opc;
using Opc.Da;

using System;


namespace OpcCom.Da
{
    [Serializable]
    internal sealed class BrowsePosition : Opc.Da.BrowsePosition
    {
        internal string ContinuationPoint;
        internal bool MoreElements;

        internal BrowsePosition(ItemIdentifier itemID, BrowseFilters filters, string continuationPoint)
          : base(itemID, filters)
        {
            ContinuationPoint = continuationPoint;
        }
    }
}
