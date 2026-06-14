// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.

using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shuttles.Events
{
    /// <summary>
    /// Raised on the client when it wishes to change the service flags of a ship.
    /// </summary>
    [Serializable, NetSerializable]
    public sealed class SetServiceFlagsRequest : BoundUserInterfaceMessage
    {
        public NetEntity? ShuttleEntityUid { get; set; }
        public ServiceFlags ServiceFlags { get; set; }
    }

    [Flags]
    [Serializable, NetSerializable]
    public enum ServiceFlags : byte
    {
        None = 0,
        Services = 1 << 0, // Medical, dining, engineering services
        Trade = 1 << 1,   // Goods/shopping available
        Social = 1 << 2   // Social gathering space
    }
}
