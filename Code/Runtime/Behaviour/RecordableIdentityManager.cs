using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.Assertions;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace ICKX.Radome {

    public class RecordableIdentityManager : ManagerBase<RecordableIdentityManager> {

        private List<RecordableIdentity> m_spawnedIdentityList = null;

        private List<System.Action<int>> uncheckReserveNetIdCallbacks; 

        public IReadOnlyList<RecordableIdentity> spawnedIdentityList {
            get { return m_spawnedIdentityList; }
        }

		public int SpawnedIdentityCount {
			get { return m_spawnedIdentityList == null ? 0 : m_spawnedIdentityList.Count; }
		} 

        public static long currentUnixTime { get; private set; }

        public static uint progressTimeSinceStartup {
            get { return (uint)(currentUnixTime - GamePacketManager.LeaderStartTime); }
        }

        private void OnEnable () {
            uncheckReserveNetIdCallbacks = new List<System.Action<int>> (4);
            GamePacketManager.OnRecievePacket += OnRecievePacket;
        }

        private void OnDisable () {
            GamePacketManager.OnRecievePacket -= OnRecievePacket;
        }

        private void Update () {
            currentUnixTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();
        }

        /// <summary>
        /// Sceneに存在するIdentityすべてを登録しなおす
        /// </summary>
        public static void ResetSpawnedIdentityList () {
            if (Instance == null) return;

            var identitys = FindObjectsOfType<RecordableIdentity> ();
            Instance.m_spawnedIdentityList = new List<RecordableIdentity> (identitys.Length);
            foreach (var identity in identitys) {
                while (identity.netId >= Instance.m_spawnedIdentityList.Count) Instance.m_spawnedIdentityList.Add (null);
                Instance.m_spawnedIdentityList[identity.netId] = identity;
                identity.SyncComplete ();
            }
        }

        /// <summary>
        /// Hostに問い合わせて重複しないNetIDを取得する.
        /// </summary>
        public static void ReserveNetId (System.Action<int> onReserveNetId) {
            if (Instance == null) return;
			if (Instance.m_spawnedIdentityList == null) return;

            if (GamePacketManager.IsLeader) {
                onReserveNetId (Instance.m_spawnedIdentityList.Count);
            } else {
                Instance.uncheckReserveNetIdCallbacks.Add (onReserveNetId);
                using (var packet = new DataStreamWriter (1, Allocator.Temp)) {
                    packet.Write ((byte)BuiltInPacket.Type.ReserveNetId);
                    GamePacketManager.Send (0, packet, QosType.Reliable);
                }
            }
        }

        /// <summary>
        /// ReserveNetIdで確保したNetIDでidentityを登録する
        /// このメソッド単体でSpawnは行わないので、それぞれのclientでIdentityを生成した後で実行すること
        /// </summary>
        public static void RegisterIdentity (RecordableIdentity identity, int netId, ushort author) {
            if (Instance == null) return;
            if (identity == null) return;
			if (Instance.m_spawnedIdentityList == null) return;

			while (identity.netId >= Instance.m_spawnedIdentityList.Count) Instance.m_spawnedIdentityList.Add (null);
            Instance.m_spawnedIdentityList[identity.netId] = identity;
            identity.SetNetId (netId);
            identity.SetAuthor (author);
            identity.SyncComplete ();
        }

        /// <summary>
        /// Hostに問い合わせて問題なければAuthorを変更する
        /// </summary>
        public static void RequestChangeAuthor (RecordableIdentity identity, ushort author) {
            if (Instance == null) return;
            if (identity == null) return;
			if (Instance.m_spawnedIdentityList == null) return;

			if (GamePacketManager.IsLeader) {
                identity.SetAuthor (author);
            } else {
                using (var packet = new DataStreamWriter (7, Allocator.Temp)) {
                    packet.Write ((byte)BuiltInPacket.Type.ChangeAuthor);
                    packet.Write (identity.netId);
                    packet.Write (author);
                    GamePacketManager.Send (0, packet, QosType.Reliable);
                }
            }
        }

        private void OnRecievePacket (ushort senderPlayerId, byte type, DataStreamReader rpcPacket, DataStreamReader.Context ctx) {
			if (m_spawnedIdentityList == null) return;

			switch ((BuiltInPacket.Type)type) {
                case BuiltInPacket.Type.ReserveNetId:
                    if (GamePacketManager.IsLeader) {
                        //HostではNetIDの整合性を確認
                        var reserveNetId = m_spawnedIdentityList.Count;
                        m_spawnedIdentityList.Add (null);

                        //Clientに通達する
                        using (var packet = new DataStreamWriter (6, Allocator.Temp)) {
                            packet.Write ((byte)BuiltInPacket.Type.ReserveNetId);
                            packet.Write (reserveNetId);
                            GamePacketManager.Send (senderPlayerId, packet, QosType.Reliable);
                        }
                    } else {
                        //確認されたauthorの変更を反映
                        var reserveNetId = rpcPacket.ReadInt(ref ctx);
                        if(uncheckReserveNetIdCallbacks.Count > 0) {
                            uncheckReserveNetIdCallbacks[0] (reserveNetId);
                            uncheckReserveNetIdCallbacks.RemoveAt (0);
                        }else {
                            Debug.LogError ("uncheckReserveNetIdCallbacks is 0");
                        }
                    }
                    break;
                case BuiltInPacket.Type.ChangeAuthor:
                    var netId = rpcPacket.ReadInt (ref ctx);
                    var author = rpcPacket.ReadUShort (ref ctx);

                    if (GamePacketManager.IsLeader) {
                        //Hostではauthorの整合性を確認
                        if (netId < m_spawnedIdentityList.Count) {
                            m_spawnedIdentityList[netId].SetAuthor (author);
                            //Clientに通達する
                            using (var packet = new DataStreamWriter (7, Allocator.Temp)) {
                                packet.Write ((byte)BuiltInPacket.Type.ChangeAuthor);
                                packet.Write (netId);
                                packet.Write (author);
                                GamePacketManager.Brodcast (packet, QosType.Reliable);
                            }
                        }
                    } else {
                        //確認されたauthorの変更を反映
                        if (netId < m_spawnedIdentityList.Count) {
                            m_spawnedIdentityList[netId].SetAuthor (author);
                        }
                    }
                    break;
                case BuiltInPacket.Type.SyncTransform:
                    var syncTranformNetId = rpcPacket.ReadInt (ref ctx);

                    if (syncTranformNetId < m_spawnedIdentityList.Count) {
                        var identity = m_spawnedIdentityList[syncTranformNetId];
                        if (identity != null) {
                            identity.OnRecieveSyncTransformPacket (senderPlayerId, ref rpcPacket, ref ctx);
                        }
                    }
                    break;
                case BuiltInPacket.Type.BehaviourRpc:
                    int rpcNetId = rpcPacket.ReadInt(ref ctx);

                    if (rpcNetId < m_spawnedIdentityList.Count) {
                        var identity = m_spawnedIdentityList[rpcNetId];
                        if (identity != null) {
                            identity.OnRecieveRpcPacket (senderPlayerId, ref rpcPacket, ref ctx);
                        }
                    }
                    break;
            }
        }

#if UNITY_EDITOR
        [CustomEditor (typeof (RecordableIdentityManager))]
        public class RecordableIdentityManagerEditor : Editor {

            public override void OnInspectorGUI () {
                base.OnInspectorGUI ();

                if (GUILayout.Button ("Assign netID all", EditorStyles.miniButton)) {
					AssignNetIDAll ();
				}
            }

			[MenuItem("ICKX/Network/AssignNetIDAll")]
			public static void AssignNetIDAll () {
				int id = 0;
				foreach (var identity in Resources.FindObjectsOfTypeAll<RecordableIdentity> ()) {
					//scenenに展開しているidentityのみ対応
					if (string.IsNullOrEmpty (identity.gameObject.scene.name)) continue;

					identity.SetNetId (id);
					id++;
				}

				EditorSceneManager.MarkAllScenesDirty ();
				Debug.Log ("Complete assign netID all.");
			}
		}
#endif
    }
}
