using System.Collections;
using System.Collections.Generic;
using System.Net;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;
using UdpCNetworkDriver = Unity.Networking.Transport.BasicNetworkDriver<Unity.Networking.Transport.IPv4UDPSocket>;

namespace ICKX.Radome {

    public class ClientNetworkManager : NetworkManagerBase {

        public override bool isFullMesh => false;

        public NetworkLinkerHandle networkLinkerHandle;

        private IPAddress serverAdress;
        private int serverPort;

        private NativeList<ushort> uncheckRegisterPlayerIds;
        private NativeList<ushort> uncheckUnregisterPlayerIds;

        public ClientNetworkManager () : base () {
            uncheckRegisterPlayerIds = new NativeList<ushort> (4, Allocator.Persistent);
            uncheckUnregisterPlayerIds = new NativeList<ushort> (4, Allocator.Persistent);
        }

        public override void Dispose () {
            if (state != State.Offline) {
                StopComplete ();
            }
            uncheckRegisterPlayerIds.Dispose ();
            uncheckUnregisterPlayerIds.Dispose ();
            driver.Dispose ();
            base.Dispose ();
        }

        /// <summary>
        /// クライアント接続開始
        /// </summary>
        public void Start (IPAddress adress, int port) {
            serverAdress = adress;
            serverPort = port;

            if (!driver.IsCreated) {
                var parm = new NetworkConfigParameter () {
                    connectTimeoutMS = 1000 * 5,
                    disconnectTimeoutMS = 1000 * 5,
                };
                driver = new UdpCNetworkDriver (new INetworkParameter[] { parm });
            }

            var endpoint = new IPEndPoint (serverAdress, port);
            state = State.Connecting;

            networkLinkerHandle = NetworkLinkerPool.CreateLinkerHandle (driver, driver.Connect (endpoint));

            Debug.Log ("StartClient");
        }

        //再接続
        private void Reconnect () {
            var endpoint = new IPEndPoint (serverAdress, serverPort);
            state = State.Connecting;

            var linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);
            linker.Reconnect (driver.Connect (endpoint));
            Debug.Log ("Reconnect");
        }

        /// <summary>
        /// クライアント接続停止
        /// </summary>
        public override void Stop () {
            if (!jobHandle.IsCompleted) {
                Debug.LogError ("NetworkJob実行中に停止できない");
                return;
            }
            state = State.Disconnecting;

            //Playerリストから削除するリクエストを送る
            using (var unregisterPlayerPacket = new DataStreamWriter (4, Allocator.Temp)) {
                unregisterPlayerPacket.Write ((byte)BuiltInPacket.Type.UnregisterPlayer);
                unregisterPlayerPacket.Write (playerId);
                Send (ServerPlayerId, unregisterPlayerPacket, QosType.Reliable);
            }
            Debug.Log ("Stop");
        }

        // サーバーから切断されたらLinkerを破棄して停止
        public override void StopComplete () {
            playerId = 0;
            state = State.Offline;
            jobHandle.Complete ();

            //var linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);
            //driver.Disconnect (linker.connection);    //ここはserverからDisconnectされたら行う処理

            NetworkLinkerPool.ReleaseLinker (networkLinkerHandle);
            networkLinkerHandle = default;
            Debug.Log ("StopComplete");
        }

        /// <summary>
        /// Player1人にパケットを送信
        /// </summary>
        public override ushort Send (ushort targetPlayerId, DataStreamWriter data, QosType qos, bool noChunk = false) {
            if (state == State.Offline) {
                Debug.LogError ("Send Failed : State.Offline");
                return 0;
            }
            ushort seqNum = 0;
            using (var writer = CreateSendPacket (data, qos, targetPlayerId, playerId)) {
                if (networkLinkerHandle.IsCreated) {
                    NetworkLinker linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);
                    if (linker != null) {
                         seqNum = linker.Send (writer, qos);
                    }
                } else {
                    Debug.LogError ("Send Failed : is not create networkLinker ID = " + targetPlayerId);
                }
            }
            return seqNum;
        }

        /// <summary>
        /// 全Playerにパケットを送信
        /// </summary>
        public override void Brodcast (DataStreamWriter data, QosType qos, bool noChunk = false) {
            if (state == State.Offline) {
                Debug.LogError ("Send Failed : State.Offline");
                return;
            }
            Send (ushort.MaxValue, data, qos, noChunk);
        }

        /// <summary>
        /// Player1人にパケットを送信 受け取り確認可能
        /// </summary>
        public override void SendReliable (ushort playerId, DataStreamWriter data, QosType qos, System.Action<ushort> onComplete, bool noChunk = false) {
            throw new System.NotImplementedException ();
        }

        /// <summary>
        /// 全Playerにパケットを送信 受け取り確認可能
        /// </summary>
        public override void BrodcastReliable (DataStreamWriter data, QosType qos, System.Action<ushort> onComplete, bool noChunk = false) {
            throw new System.NotImplementedException ();
            //サーバーに到達したら確定とする
        }

        /// <summary>
        /// 受信パケットの受け取りなど、最初に行うべきUpdateループ
        /// </summary>
        public override void OnFirstUpdate () {
            jobHandle.Complete ();
            if (!networkLinkerHandle.IsCreated) {
                return;
            }

            var linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);
            linker.Complete ();

            if (state == State.Offline) {
                return;
            }

            //受け取ったパケットを処理に投げる.
            if (!networkLinkerHandle.IsCreated) return;

            if (linker.IsConnected) {
                Debug.Log ("IsConnected : dataLen=" + linker.dataStreams.Length);
            }
            if (linker.IsDisconnected) {
                Debug.Log ("IsDisconnected");
                if(state == State.Disconnecting) {
                    StopComplete ();
                }else {
                    Reconnect ();
                }
                return;
            }

            for (int j = 0; j < linker.dataStreams.Length; j++) {
                var stream = linker.dataStreams[j];
                if (!stream.IsCreated) return;

                var ctx = default (DataStreamReader.Context);
                byte qosType = stream.ReadByte (ref ctx);
                ushort seqNum = stream.ReadUShort (ref ctx);
                ushort ackNum = stream.ReadUShort (ref ctx);

                while (true) {
                    int pos = stream.GetBytesRead (ref ctx);
                    if (pos >= stream.Length) break;
                    ushort dataLength = stream.ReadUShort (ref ctx);
                    if (dataLength == 0) break;

                    var chunk = stream.ReadChunk (ref ctx, dataLength);
                    var ctx2 = default (DataStreamReader.Context);

                    ushort targetPlayerId = chunk.ReadUShort (ref ctx2);
                    ushort senderPlayerId = chunk.ReadUShort (ref ctx2);
                    byte type = chunk.ReadByte (ref ctx2);
                    //Debug.Log ("Linker streamLen=" + stream.Length + ", Pos=" + pos + ", chunkLen=" + chunk.Length + ",type=" + type + ",target=" + targetPlayerId + ",sender=" + senderPlayerId);

                    //自分宛パケットの解析
                    switch (type) {
                        case (byte)BuiltInPacket.Type.RegisterPlayer:
                            state = State.Online;
                            playerId = chunk.ReadUShort (ref ctx2);
                            leaderStatTime = chunk.ReadLong (ref ctx2);

                            ushort syncSelfSeqNum = chunk.ReadUShort (ref ctx2);
                            if (syncSelfSeqNum != 0) {
                                Debug.Log ("Reconnected");
                            }
                            linker.SyncSeqNum (syncSelfSeqNum);

                            byte count = chunk.ReadByte (ref ctx2);
                            for (int i = 0; i < count; i++) {
                                activePlayerIdList.Add( chunk.ReadByte(ref ctx2));
                            }
                            for (int i = 0; i < uncheckRegisterPlayerIds.Length; i++) {
                                RegisterPlayerId (uncheckRegisterPlayerIds[i]);
                            }
                            for (int i = 0; i < uncheckUnregisterPlayerIds.Length; i++) {
                                UnregisterPlayerId (uncheckUnregisterPlayerIds[i]);
                            }
                            uncheckRegisterPlayerIds.Clear ();
                            uncheckUnregisterPlayerIds.Clear ();
                            break;
                        case (byte)BuiltInPacket.Type.NotifyAddPlayer:
                            ushort addPlayerId = chunk.ReadUShort (ref ctx2);
                            if (state == State.Online) {
                                if (addPlayerId != playerId) {
                                    RegisterPlayerId (addPlayerId);
                                    ExecOnRegisterPlayer (addPlayerId);
                                }
                            }else {
                                //ほかの接続情報が順番入れ替わっている可能性を考慮
                                uncheckRegisterPlayerIds.Add (addPlayerId);
                                Debug.Log ("uncheckRegisterPlayerIds.Add");
                            }
                            Debug.Log ("NotifyAddPlayer id=" + addPlayerId);
                            break;
                        case (byte)BuiltInPacket.Type.NotifyRemovePlayer:
                            ushort removePlayerId = chunk.ReadUShort (ref ctx2);
                            if (state == State.Online) {
                                if (removePlayerId != playerId) {
                                    UnregisterPlayerId (removePlayerId);
                                    ExecOnUnregisterPlayer (removePlayerId);
                                }
                            } else {
                                //ほかの接続情報が順番入れ替わっている可能性を考慮して
                                uncheckUnregisterPlayerIds.Add (removePlayerId);
                                Debug.Log ("uncheckUnregisterPlayerIds.Add");
                            }
                            Debug.Log ("NotifyRemovePlayer id=" + removePlayerId);
                            break;
                        case (byte)BuiltInPacket.Type.StopNetwork:
                            Stop ();
                            break;
                        default:
                            ExecOnRecievePacket (senderPlayerId, type, chunk, ctx2);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// まとめたパケット送信など、最後に行うべきUpdateループ
        /// </summary>
        public override void OnLastUpdate () {
            if (state == State.Offline) {
                return;
            }

            if (!networkLinkerHandle.IsCreated) {
                return;
            }
            var linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);

            if (state == State.Online || state == State.Disconnecting) {
                linker.SendMeasureLatencyPacket ();
                linker.SendReliableChunks ();
            }

            jobHandle = linker.ScheduleSendUnreliableChunks (default (JobHandle));

            jobHandle = driver.ScheduleUpdate (jobHandle);

            jobHandle = linker.ScheduleRecieve (jobHandle);
        }
    }
}
