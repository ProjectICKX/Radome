using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Assertions;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace ICKX.Radome {
    public class RecordableIdentity : MonoBehaviour {

        public delegate void OnChangeAuthorEvent (RecordableIdentity identity, ushort author, bool hasAuthority);

        //[SerializeField]
        //private RecordableIdentity[] m_childrenIdentity;
        //[SerializeField]
        //private bool m_isChildIdentity;

        [Disable]
        [SerializeField]
        private int m_netId = -1;

        public int netId { get { return m_netId; } }

        public ushort author { get; private set; } = 0;

        public ushort gridId { get; private set; }

        public bool isSyncComplete { get; private set; }

        public bool hasAuthority {
            get {
                return GamePacketManager.PlayerId == author;
            }
        }

        public event OnChangeAuthorEvent OnChangeAuthor = null;

        public Transform CacheTransform { get; private set; }
        public RecordableTransform CacheRecordableTransform { get; private set; }

        private List<RecordableBehaviour> m_recordableBehaviourList;

        private void Awake () {
            CacheTransform = transform;
            CacheRecordableTransform = GetComponent<RecordableTransform> ();

            var behaviours = GetComponents<RecordableBehaviour> ();
            m_recordableBehaviourList = new List<RecordableBehaviour>(behaviours.Length);
            foreach (var component in behaviours) {
                while (component.componentIndex >= m_recordableBehaviourList.Count) {
                    m_recordableBehaviourList.Add (null);
                }
                m_recordableBehaviourList[component.componentIndex] = component;
            }
        }

        private void LateUpdate () {
            UpdateGridId ();
        }

        //空間分割してパケットをフィルタするためのGridIDを計算する.
        private void UpdateGridId () {
            //あとで作る
        }

        internal void SetNetId (int netId) {
            this.m_netId = netId;
        }


        internal void SetAuthor (ushort author) {
            this.author = author;
            OnChangeAuthor (this, author, hasAuthority);
        }

        internal void SyncComplete () {
            isSyncComplete = true;
        }

        internal byte AddRecordableBehaviour (RecordableBehaviour recordableBehaviour) {
            m_recordableBehaviourList.Add (recordableBehaviour);
            return (byte)m_recordableBehaviourList.Count;
        }

        internal void SendRpc (ushort targetPlayerId, byte componentIndex, DataStreamWriter rpcPacket, QosType qosType, bool important) {
            if (!isSyncComplete) return;
            using (var writer = new DataStreamWriter (rpcPacket.Length + 6, Allocator.Temp)) {
                unsafe {
                    byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr (rpcPacket);
                    writer.Write ((byte)BuiltInPacket.Type.BehaviourRpc);
                    writer.Write (netId);
                    writer.Write (componentIndex);
                    writer.WriteBytes (dataPtr, rpcPacket.Length);
                }
                GamePacketManager.Send (targetPlayerId, writer, qosType);
                //if (important) {
                //    GamePacketManager.Send (playerId, writer, qosType);
                //} else {
                //    GamePacketManager.Send (playerId, writer, qosType, gridId);
                //}
            }
        }

        internal void BrodcastRpc (byte componentIndex, DataStreamWriter rpcPacket, QosType qosType, bool important) {
            if (!isSyncComplete) return;
            using (var writer = new DataStreamWriter (rpcPacket.Length + 6, Allocator.Temp)) {
                unsafe {
                    byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr (rpcPacket);
                    writer.Write ((byte)BuiltInPacket.Type.BehaviourRpc);
                    writer.Write (netId);
                    writer.Write (componentIndex);
                    writer.WriteBytes (dataPtr, rpcPacket.Length);
                }
                GamePacketManager.Brodcast (writer, qosType);
                //if (important) {
                //    GamePacketManager.Brodcast (writer, qosType);
                //} else {
                //    GamePacketManager.Brodcast (writer, qosType, gridId);
                //}
            }
        }


        internal void OnRecieveSyncTransformPacket (ushort senderPlayerId, ref DataStreamReader packet, ref DataStreamReader.Context ctx) {
            if (!isSyncComplete) return;

            if (CacheRecordableTransform) {
                CacheRecordableTransform.OnRecieveSyncTransformPacket (senderPlayerId, ref packet, ref ctx);
            }
        }


        internal void OnRecieveRpcPacket (ushort senderPlayerId, ref DataStreamReader rpcPacket, ref DataStreamReader.Context ctx) {
            if (!isSyncComplete) return;

            byte componentIndex = rpcPacket.ReadByte(ref ctx);
            byte methodId = rpcPacket.ReadByte (ref ctx);

            if (componentIndex < m_recordableBehaviourList.Count) {
                var behaviour = m_recordableBehaviourList[componentIndex];
                if (behaviour == null) return;
                behaviour.OnRecieveRpcPacket (senderPlayerId, methodId, rpcPacket, ctx);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="syncPacket"></param>
        internal void CollectSyncVarPacket (ref DataStreamWriter syncPacket) {
            //あとで作る
        }

        internal void ApplySyncVarPacket (ref DataStreamReader syncPacket, ref DataStreamReader.Context ctx) {
            //あとで作る
        }

#if UNITY_EDITOR
        [CustomEditor (typeof (RecordableIdentity)), CanEditMultipleObjects]
        public class RecordableIdentityEditor : Editor {

            public override void OnInspectorGUI () {
                base.OnInspectorGUI ();

                if (Application.isPlaying && targets.Length == 1) {
                    var identity = target as RecordableIdentity;

                    EditorGUILayout.IntField ("author", identity.author);
                    EditorGUILayout.Toggle ("HasAuthority", identity.hasAuthority);
                    EditorGUILayout.Toggle ("IsSyncComplete", identity.isSyncComplete);
                }
            }
        }
#endif
    }
}
