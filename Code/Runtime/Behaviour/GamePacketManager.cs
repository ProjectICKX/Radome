using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using static ICKX.Radome.NetworkManagerBase;

namespace ICKX.Radome {

    public class GamePacketManager {

        [RuntimeInitializeOnLoadMethod]
        static void Initialize () {
            localStartTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();
        }

        public static NetworkManagerBase NetworkManager { get; private set; } = null;

        public static event OnRegisterPlayerEvent OnRegisterPlayer = null;
        public static event OnUnregisterPlayerEvent OnUnregisterPlayer = null;
        public static event OnRecievePacketEvent OnRecievePacket = null;

        public static bool IsLeader {
            get {
                if (NetworkManager == null) {
                    return true;
                } else {
                    return NetworkManager.isLeader;
                }
            }
        }

        public static ushort PlayerId {
            get {
                if(NetworkManager == null) {
                    return 0;
                }else {
                    return NetworkManager.playerId;
                }
            }
        }

        private static long localStartTime;

        public static long LeaderStartTime {
            get {
                if (NetworkManager == null) {
                    return localStartTime;
                } else {
                    return NetworkManager.leaderStatTime;
                }
            }
        }

        public static void SetNetworkManager (NetworkManagerBase networkManager) {
            if(networkManager != null) {
                networkManager.OnRegisterPlayer -= OnRegisterPlayerMethod;
                networkManager.OnUnregisterPlayer -= OnUnregisterPlayerMethod;
                networkManager.OnRecievePacket -= OnRecievePacketMethod;
            }
            NetworkManager = networkManager;
			
            networkManager.OnRegisterPlayer += OnRegisterPlayerMethod;
            networkManager.OnUnregisterPlayer += OnUnregisterPlayerMethod;
            networkManager.OnRecievePacket += OnRecievePacketMethod;
        }

        private static void OnRegisterPlayerMethod (ushort id) {
            OnRegisterPlayer?.Invoke (id);
        }

        private static void OnUnregisterPlayerMethod (ushort id) {
            OnUnregisterPlayer?.Invoke (id);
        }

        private static void OnRecievePacketMethod (ushort senderPlayerId, byte type, DataStreamReader stream, DataStreamReader.Context ctx) {
            OnRecievePacket?.Invoke (senderPlayerId, type, stream, ctx);
        }

		public static void Send (ushort playerId, DataStreamWriter data, QosType qos, bool noChunk = false) {
			if (NetworkManager == null || NetworkManager.state == State.Offline) return;
			NetworkManager.Send (playerId, data, qos, noChunk);
		}

		public static void Send (NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos, bool noChunk = false) {
			//if (NetworkManager == null || NetworkManager.state == State.Offline) return;
			//NetworkManager.Send (playerIdList, data, qos, noChunk);
			throw new System.NotImplementedException ();
		}

		public static void Brodcast (DataStreamWriter data, QosType qos, bool noChunk = false) {
            if (NetworkManager == null || NetworkManager.state == State.Offline) return;
            NetworkManager.Brodcast (data, qos, noChunk);
        }

    }
}
