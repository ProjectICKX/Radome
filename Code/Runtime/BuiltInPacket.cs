using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;

namespace ICKX.Radome {
    public static class BuiltInPacket {
        public enum Type : byte {
            MeasureRtt = 200,
            RegisterPlayer,
            UnregisterPlayer,
            NotifyAddPlayer,
            NotifyRemovePlayer,
            StopNetwork,

            ReserveNetId,
            ChangeAuthor,
            SyncTransform,
            BehaviourRpc,

			DataTransporter,
        }
    }
}
