# 概要
RadomeはUnity Transport Packageを利用した汎用ネットワークおよびリプレイライブラリです。
https://github.com/Unity-Technologies/multiplayer

+ 上記ライブラリで未完成のNotification Layer (Reliabilityの実現)
+ Server-Client, P2P, Relay Server利用を想定したNetworkBaseクラス
+ 旧UnetのHLAPIの主要機能 (NetIDによるGameObjectの同期, Transformの同期, RPCなど)
+ 通信パケットを利用したリプレイ機能 (工事中)

を提供します。

# 簡易マニュアル

## Package Managerによるインストール

Packageフォルダにあるmanifest.jsonのdependenciesにあるパッケージに以下のように記載してください。
(jp.ickx.commonはProjectICKXのUnityライブラリで汎用的に利用する機能をまとめたパッケージです)

```
{
  "dependencies": {
    ...
    "com.unity.mathematics": "0.0.12-preview.19",
    "jp.ickx.common": "https://github.com/ProjectICKX/UnityCommon.git",
    "jp.ickx.radome": "https://github.com/ProjectICKX/Radome.git",
    ...
  }
}
```
## Server-Client接続
Serverとして起動する場合
```
ServerNetworkManager serverNetwork = new ServerNetworkManager ();
serverNetwork.Start ([任意のポート番号]);
networkBase = serverNetwork;
```

Clientとして起動する場合
```
ClientNetworkManager clientNetwork = new ClientNetworkManager ();
clientNetwork.Start ([サーバーのアドレス], [サーバーのポート番号]);
networkBase = clientNetwork;
```

上記で生成したNetworkManagerBaseのインスタンスをGamePacketManagerに登録する
```
GamePacketManager.SetNetworkManager (networkBase);
GamePacketManager.OnRecievePacket += OnRecievePacket;

//パケットを受け取るコールバック
private void OnRecievePacket (ushort senderPlayerId, byte type, DataStreamReader stream, DataStreamReader.Context ctx) {

}
```


## パケットの送受信


## RecordableIdentityの利用

### RecordableTransform

### RPC通信

### 所有権の変更

### NetIDの発行とIdentityの作成


