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

    public class ServerNetworkManager : NetworkManagerBase {

        public struct PlayerInfo {
            public NetworkEndPoint endPoint;
            public State state;
            public float disconnectTime;

            public bool IsCreated { get { return endPoint.IsValid; } }
        }

        public float registrationTimeOut { get; set; } = 60.0f;

        public override bool isFullMesh => false;

        public NativeList<PlayerInfo> activePlayerInfoList;
        public NativeList<NetworkLinkerHandle> networkLinkerHandles;

        public event OnReconnectPlayerEvent OnReconnectPlayer = null;
        public event OnDisconnectPlayerEvent OnDisconnectPlayer = null;

        public ServerNetworkManager () : base () {
            activePlayerInfoList = new NativeList<PlayerInfo> (8, Allocator.Persistent);
        }

        public override void Dispose () {
            if (state != State.Offline) {
                StopComplete ();
            }
            activePlayerInfoList.Dispose ();
            if(driver.IsCreated) {
                driver.Dispose ();
            }
            base.Dispose ();
        }

        /// <summary>
        /// サーバー起動
        /// </summary>
        public void Start (int port) {
            if(state != State.Offline) {
                Debug.LogError ("Start Failed  currentState = " + state);
                return;
            }

            leaderStatTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();

            if (!driver.IsCreated) {
                var parm = new NetworkConfigParameter () {
                    connectTimeoutMS = 1000 * 5,
                    disconnectTimeoutMS = 1000 * 5,
                };
                driver = new UdpCNetworkDriver (new INetworkParameter[] { parm });
            }

            state = State.Connecting;
            var endPoint = new IPEndPoint (IPAddress.Any, port);
            if (driver.Bind (endPoint) != 0) {
                Debug.Log ("Failed to bind to port 9000");
            } else {
                driver.Listen ();
            }

            networkLinkerHandles = new NativeList<NetworkLinkerHandle> (16, Allocator.Persistent);
            //networkLinkerHandles.Add (default);
            RegisterPlayerId (0, endPoint, default(NetworkConnection));
            Debug.Log ("StartServer");
        }

        /// <summary>
        /// サーバー停止
        /// </summary>
        public override void Stop () {
            if (state == State.Offline) {
                Debug.LogError ("Start Failed  currentState = " + state);
                return;
            }
            if (!jobHandle.IsCompleted) {
                Debug.LogError ("NetworkJob実行中に停止できない");
                return;
            }

            state = State.Disconnecting;

            //すべてのPlayerに停止を伝えてからサーバーも停止
            if(GetPlayerCount() == 1) {
                StopComplete ();
            }else {
                SendStopNetworkPacket ();
                Debug.Log ("Stop");
            }
        }

        // すべてのClientが切断したら呼ぶ
        public override void StopComplete () {
            if (state == State.Offline) {
                Debug.LogError ("CompleteStop Failed  currentState = " + state);
                return;
            }

            state = State.Offline;
            jobHandle.Complete ();

            driver.Dispose ();

            if (networkLinkerHandles.IsCreated) {
                for (int i = 0; i < networkLinkerHandles.Length; i++) {
                    if (networkLinkerHandles[i].IsCreated) {
                        NetworkLinkerPool.ReleaseLinker (networkLinkerHandles[i]);
                        networkLinkerHandles[i] = default;
                    }
                }
                networkLinkerHandles.Dispose ();
            }
            Debug.Log ("StopComplete");
        }

        //新しいPlayerを登録する処理
        protected void RegisterPlayerId (ushort id, NetworkEndPoint endPoint, NetworkConnection connection) {
            base.RegisterPlayerId (id);
            Debug.Log ("RegisterPlayerId " + id);

            var playerInfo = new PlayerInfo () {
                endPoint = endPoint,
                state = State.Online
            };

            if (id == activePlayerInfoList.Length) {
                activePlayerInfoList.Add (playerInfo);
            } else {
                activePlayerInfoList[id] = playerInfo;
            }

            if (id == 0) {
                networkLinkerHandles.Add (default);
                return;
            }

            var handle = NetworkLinkerPool.CreateLinkerHandle (driver, connection);
            if (id == networkLinkerHandles.Length) {
                networkLinkerHandles.Add (handle);
            } else {
                networkLinkerHandles[id] = handle;
            }

            //playerIDを通知するパケットを送信.
            SendRegisterPlayerPacket (id);

            //他の接続済みplayerに通知
            SendNotifyAddPlayerPacket (id);

            ExecOnRegisterPlayer (id);
        }
        
        //Playerを登録解除する処理
        protected new void UnregisterPlayerId (ushort id) {
            base.UnregisterPlayerId (id);
            Debug.Log ("UnregisterPlayerId " + id);

            if (id < activePlayerInfoList.Length) {
                var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[id]);
                driver.Disconnect (linker.connection);

                NetworkLinkerPool.ReleaseLinker (networkLinkerHandles[id]);
                networkLinkerHandles[id] = default;
                activePlayerInfoList[id] = default;
            }

            if (state == State.Disconnecting) {
                //すべて切断したらサーバー完全停止.
                if(GetPlayerCount() == 1) {
                    StopComplete ();
                }
            } else {
                //接続済みplayerに通知
                SendNotifyRemovePlayerPacket (id);
            }
            ExecOnUnregisterPlayer (id);
        }

        //Playerを再接続させる処理
        protected void ReconnectPlayerId (ushort id, NetworkConnection connection) {
            Debug.Log ("ReconnectPlayerId " + id);

            if (id < activePlayerInfoList.Length) {
                var info = activePlayerInfoList[id];
                if(info.IsCreated) {
                    info.state = State.Online;
                    activePlayerInfoList[id] = info;
                }
                var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[id]);
                linker.Reconnect (connection);

                //playerIDを通知するパケットを送信.
                SendRegisterPlayerPacket (id);
            }
            OnReconnectPlayer?.Invoke (id);
        }

        //Playerを一旦切断状態にする処理
        protected void DisconnectPlayerId (ushort id) {
            Debug.Log ("DisconnectPlayerId " + id);

            if (id < activePlayerInfoList.Length) {
                var info = activePlayerInfoList[id];
                if (info.IsCreated) {
                    info.state = State.Connecting;
                    info.disconnectTime = Time.realtimeSinceStartup;
                    activePlayerInfoList[id] = info;
                }
            }
            OnDisconnectPlayer?.Invoke (id);
        }

        /// <summary>
        /// Player1人にパケットを送信
        /// </summary>
        public override ushort Send (ushort targetPlayerId, DataStreamWriter data, QosType qos, bool noChunk = false) {
            if (state == State.Offline) {
                Debug.LogError ("Send Failed : State." + state);
                return 0;
            }
            ushort seqNum = 0;
            using (var writer = CreateSendPacket (data, qos, targetPlayerId, playerId)) {
                if (targetPlayerId < networkLinkerHandles.Length && networkLinkerHandles[targetPlayerId].IsCreated) {
                    NetworkLinker linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[targetPlayerId]);
                    if (linker != null) {
                        seqNum = linker.Send (writer, qos, noChunk);
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
                Debug.LogError ("Send Failed : State." + state);
                return;
            }
            using (var writer = CreateSendPacket (data, qos, ushort.MaxValue, playerId)) {
                for (int i = 1; i < networkLinkerHandles.Length; i++) {
                    if (networkLinkerHandles[i].IsCreated) {
                        NetworkLinker linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[i]);
                        if (linker != null) {
                            linker.Send (writer, qos, noChunk);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Player1人にパケットを送信 受け取り確認可能
        /// </summary>
        public override void SendReliable (ushort id, DataStreamWriter data, QosType qos, System.Action<ushort> onComplete, bool noChunk = false) {
            throw new System.NotImplementedException ();
        }

        /// <summary>
        /// 全Playerにパケットを送信 受け取り確認可能
        /// </summary>
        public override void BrodcastReliable (DataStreamWriter data, QosType qos, System.Action<ushort> onComplete, bool noChunk = false) {
            throw new System.NotImplementedException ();
            //全員に到達したら確定とする
        }

        private void SendRegisterPlayerPacket (ushort id) {
            var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[id]);

            using (var registerPacket = new DataStreamWriter (14 + activePlayerIdList.Length, Allocator.Temp)) {
                registerPacket.Write ((byte)BuiltInPacket.Type.RegisterPlayer);
                registerPacket.Write (id);
                registerPacket.Write (leaderStatTime);
                registerPacket.Write (linker.OtherSeqNumber);
                registerPacket.Write ((byte)activePlayerIdList.Length);
                for (int i = 0; i < activePlayerIdList.Length; i++) {
                    registerPacket.Write (activePlayerIdList[i]);
                }
                Send (id, registerPacket, QosType.Reliable);
            }
        }

        private void SendNotifyAddPlayerPacket (ushort id) {
            using (var addPlayerPacket = new DataStreamWriter (4, Allocator.Temp)) {
                addPlayerPacket.Write ((byte)BuiltInPacket.Type.NotifyAddPlayer);
                addPlayerPacket.Write (id);
                Brodcast (addPlayerPacket, QosType.Reliable);
            }
        }

        private void SendNotifyRemovePlayerPacket (ushort id) {
            using (var removePlayerPacket = new DataStreamWriter (4, Allocator.Temp)) {
                removePlayerPacket.Write ((byte)BuiltInPacket.Type.NotifyRemovePlayer);
                removePlayerPacket.Write (id);
                Brodcast (removePlayerPacket, QosType.Reliable);
            }
        }

        private void SendStopNetworkPacket () {
            using (var stopNetworkPacket = new DataStreamWriter (2, Allocator.Temp)) {
                stopNetworkPacket.Write ((byte)BuiltInPacket.Type.StopNetwork);
                stopNetworkPacket.Write ((byte)0);    //TODO error code

                Brodcast (stopNetworkPacket, QosType.Reliable, true);
            }
        }

        /// <summary>
        /// 今までに接続してきたplayerのEndPointがあればPlayerIDを返す
        /// </summary>
        public ushort ContainEndPointPlayerInfoList (NetworkEndPoint endPoint) {
            for (ushort i = 0; i < networkLinkerHandles.Length; i++) {
                if (networkLinkerHandles[i].IsCreated) {
                    var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[i]);
                    var remoteEndPoint = driver.RemoteEndPoint (linker.connection);

                    if(endPoint.GetIp() == remoteEndPoint.GetIp() && endPoint.Port == remoteEndPoint.Port) {
                        return i;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// 受信パケットの受け取りなど、最初に行うべきUpdateループ
        /// </summary>
        public override void OnFirstUpdate () {
            if (state == State.Offline) {
                return;
            }

            //job完了待ち
            jobHandle.Complete ();
            for (int i = 0; i < networkLinkerHandles.Length; i++) {
                if (networkLinkerHandles[i].IsCreated) {
                    var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[i]);
                    linker.Complete ();
                }
            }

            //接続確認
            NetworkConnection connection;
            while ((connection = driver.Accept ()) != default) {
                state = State.Online;
                ushort disconnectedPlayerId = 0;

                var connState = driver.GetConnectionState (connection);
                var remoteEndPoint = driver.RemoteEndPoint (connection);
                disconnectedPlayerId = ContainEndPointPlayerInfoList (remoteEndPoint);

                if (disconnectedPlayerId != 0) {
                    //再接続として扱う
                    ReconnectPlayerId (disconnectedPlayerId, connection);
                    Debug.Log ("Accepted a reconnection  playerId=" + disconnectedPlayerId);
                } else {
                    //接続してきたクライアントとLinkerで接続
                    ushort newPlayerId = GetDeactivePlayerId ();
                    RegisterPlayerId (newPlayerId, remoteEndPoint, connection);
                    Debug.Log ("Accepted a connection  newPlayerId=" + newPlayerId);
                }
            }

            //一定時間切断したままのplayerの登録を解除
            for (ushort i = 0; i < activePlayerInfoList.Length; i++) {
                var info = activePlayerInfoList[i];
                if (info.IsCreated) {
                    if(info.state == State.Connecting) {
                        if(Time.realtimeSinceStartup - info.disconnectTime > registrationTimeOut) {
                            UnregisterPlayerId (i);
                        }
                    }
                }
            }

            //受け取ったパケットを処理に投げる.
            for (ushort i = 0; i < networkLinkerHandles.Length; i++) {
                if (!networkLinkerHandles[i].IsCreated) continue;

                var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[i]);

                if (linker.IsDisconnected) {
                    Debug.Log ("IsDisconnected");
                    //切断したClientを一時停止状態に.
                    DisconnectPlayerId (i);
                    continue;
                }

                bool finish = false;

                //受け取ったパケットを解析
                for (int j = 0; j < linker.dataStreams.Length; j++) {
                    var stream = linker.dataStreams[j];
                    if (!stream.IsCreated) continue;
                    var ctx = default (DataStreamReader.Context);

                    QosType qosType = (QosType)stream.ReadByte (ref ctx);
                    ushort seqNum = stream.ReadUShort (ref ctx);
                    ushort ackNum = stream.ReadUShort (ref ctx);

                    //chunkをバラして解析
                    while (!finish) {
                        int pos = stream.GetBytesRead (ref ctx);
                        if (pos >= stream.Length) break;
                        ushort dataLength = stream.ReadUShort (ref ctx);
                        if (dataLength == 0) break;
                        var chunk = stream.ReadChunk (ref ctx, dataLength);

                        var ctx2 = default (DataStreamReader.Context);
                        ushort targetPlayerId = chunk.ReadUShort (ref ctx2);
                        ushort senderPlayerId = chunk.ReadUShort (ref ctx2);
                        byte type = chunk.ReadByte (ref ctx2);
//                        Debug.Log ("Linker streamLen=" + stream.Length + ", Pos=" + pos + ", chunkLen=" + chunk.Length + ",type=" + type + ",target=" + targetPlayerId + ",sender=" + senderPlayerId);

                        if ((targetPlayerId != ServerPlayerId)) {
                            //パケットをリレーする
                            using (var writer = new DataStreamWriter (chunk.Length, Allocator.Temp)) {
                                unsafe {
                                    byte* chunkPtr = chunk.GetUnsafeReadOnlyPtr ();
                                    writer.WriteBytes (chunkPtr, (ushort)chunk.Length);
                                }
                                if (targetPlayerId == ushort.MaxValue) {
                                    for (int k = 1; k < networkLinkerHandles.Length; k++) {
                                        if (senderPlayerId == k) continue;
                                        var relayLinker = NetworkLinkerPool.GetLinker (networkLinkerHandles[k]);
                                        relayLinker.Send (writer, qosType);
                                    }
                                } else {
                                    var relayLinker = NetworkLinkerPool.GetLinker (networkLinkerHandles[targetPlayerId]);
                                    relayLinker.Send (writer, qosType);
                                }
                            }
                        }

                        if ((targetPlayerId == playerId || targetPlayerId == ushort.MaxValue)) {
                            //自分宛パケットの解析
                            switch (type) {
                                case (byte)BuiltInPacket.Type.UnregisterPlayer:
                                    //登録解除リクエスト
                                    ushort unregisterPlayerId = chunk.ReadUShort (ref ctx2);
                                    UnregisterPlayerId (unregisterPlayerId);
                                    finish = true;
                                    break;
                                default:
                                    //自分宛パケットの解析
                                    ExecOnRecievePacket (senderPlayerId, type, chunk, ctx2);
                                    break;
                            }
                        }
                    }
                    if(finish) {
                        if(state == State.Offline) {
                            //server停止ならUpdate完全終了
                            return;
                        }else {
                            //1 clientが停止ならパケット解析だけ終了
                            break;
                        }
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

            if (Input.GetKeyDown (KeyCode.Delete)) {
                Debug.Log ("Disconnect");
                for (int i = 0; i < networkLinkerHandles.Length; i++) {
                    if (networkLinkerHandles[i].IsCreated) {
                        var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[i]);
                        linker.driver.Disconnect (linker.connection);
                    }
                }
            }

            //まずmain thread処理
            for (int i = 0; i < networkLinkerHandles.Length; i++) {
                if (networkLinkerHandles[i].IsCreated) {
                    var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[i]);
                    if (state == State.Online || state == State.Disconnecting) {
                        linker.SendMeasureLatencyPacket ();
                        linker.SendReliableChunks ();
                    }
                }
            }

            //Unreliableなパケットの送信
            var linkerJobs = new NativeArray<JobHandle> (networkLinkerHandles.Length, Allocator.Temp);
            for (int i = 0; i < networkLinkerHandles.Length; i++) {
                if (networkLinkerHandles[i].IsCreated) {
                    var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[i]);
                    linkerJobs[i] = linker.ScheduleSendUnreliableChunks (default (JobHandle));
                }
            }
            jobHandle = JobHandle.CombineDependencies (linkerJobs);

            //driverの更新
            jobHandle = driver.ScheduleUpdate (jobHandle);

            //TODO iJobで実行するとNetworkDriverの処理が並列にできない
            //     できればIJobParallelForでScheduleRecieveを並列化したい
            for (int i = 0; i < networkLinkerHandles.Length; i++) {
                if (networkLinkerHandles[i].IsCreated) {
                    //JobスレッドでLinkerのパケット処理開始
                    var linker = NetworkLinkerPool.GetLinker (networkLinkerHandles[i]);
                    jobHandle = linker.ScheduleRecieve (jobHandle);
                }
            }
            linkerJobs.Dispose ();
        }
    }
}
