# Notify-Relay/Notify-Relay-pc 跨端接口文档

## 1. 设备发现与认证

### 1.1 UDP广播发现
- 端口：23334
- 广播包格式：`NOTIFYRELAY_DISCOVER:{uuid}:{displayName}:{port}`（`displayName` 使用 Base64(NO_WRAP) 编码传输，接收端需解码并清洗）
- 说明：移动端/PC端均需定时广播和监听此包，实现局域网内设备自动发现。

### 1.2 认证与握手
- TCP端口：23333（默认，可配置）
- 握手流程（要点）：
  1. 主动方通过 TCP 连接目标设备，发送：`HANDSHAKE:{uuid}:{localPublicKey}\n`
  2. 被动方收到握手后：
     - 如果本地已存在该设备的认证信息（包含 `publicKey` 与派生的对称密钥），被动方直接回复：`ACCEPT:{uuid}:{localPublicKey}\n`。
     - 如果未认证，被动方应触发 UI 确认（用户同意或拒绝）：
       - 用户同意：双方使用对方交换的 `publicKey` 派生对称 `sharedSecret`（见第 4 节 HKDF 约定），将 `AuthInfo`（`uuid`, `publicKey`, 本地派生的 `sharedSecret`）安全地存储到本地后，回复 `ACCEPT:{uuid}:{localPublicKey}\n`。
       - 用户拒绝：被动方回复 `REJECT:{uuid}\n` 并将该 UUID 加入拒绝列表以防止重复请求。
  3. 主动方接收 `ACCEPT` 后，也使用本地与对端的 `publicKey` 派生并存储 `sharedSecret`，并将设备标记为已认证。
  4. 认证成功后，双方可启动心跳（`HEARTBEAT`）以维持在线状态。

> 重要：**切勿在任何消息中以明文或可逆格式传输 `sharedSecret`。** 对称密钥仅在本地派生并存储（已实现：Android 使用 EncryptedSharedPreferences，PC 端应同样将敏感数据安全存储）。

### 1.3 心跳包
- 格式：`HEARTBEAT:{uuid}:{publicKey}\n`
- 说明：已认证设备间定时互发，维持在线状态。

---

## 2. 通知转发协议

### 2.1 通知数据包
- 格式（新版）：`DATA_JSON:{uuid}:{publicKey}:{encryptedPayload}\n`
- 说明：`encryptedPayload` 为对称加密后的负载，发送方**不得**在报文中包含或传输 `sharedSecret`。
- `encryptedPayload` 解构（实现约定）：Base64 编码的字节流，按顺序为 `IV || Ciphertext || Tag`，其中：
  - IV 长度：12 字节（96 位），随机生成（AES‑GCM 推荐）
  - Tag 长度：16 字节（128 位）
  - Ciphertext：由 AES‑GCM 对明文 JSON 加密得到的密文（不包含 tag）
  - 最终编码：将 IV、Ciphertext、Tag 连接后做 Base64(NO_WRAP) 作为 `encryptedPayload`

示例明文 JSON（同旧格式）：
  ```json
  {
    "packageName": "com.example.app",
    "appName": "示例App",
    "title": "通知标题",
    "text": "通知内容",
    "time": 1690000000000,
    "isLocked": false
  }
  ```
加密方式与密钥派生：
- 对称密钥 `sharedSecret` 的派生：使用 HKDF‑SHA256，输入密钥材料为双方公钥字符串的确定性拼接（按字典序拼接 `localPublicKey||remotePublicKey`），派生信息（info）字符串为 `"shared-secret"`，输出长度为 32 字节（AES‑256），并以 Base64 编码存储/传递于本地 API（不在网络上发送）。
- 对称加密算法：`AES‑GCM`（`AES/GCM/NoPadding`），IV=12 字节随机，Tag=16 字节。
- 加密报文布局见上（Base64(iv||ciphertext||tag)）。

可选安全建议：对 `encryptedPayload` 在明文 JSON 中加入时间戳/序列号并在解密后验证以抵抗重放攻击。

### 2.2 图标同步（可选）
- 请求：`DATA_ICON_REQUEST:{uuid}:{publicKey}:{encryptedPayload}\n`
- 响应：`DATA_ICON_RESPONSE:{uuid}:{publicKey}:{encryptedPayload}\n`
- 说明：与通知数据包相同，`encryptedPayload` 使用 AES‑GCM 对 JSON 负载加密，接收方使用本地存储的 `sharedSecret` 解密并处理。不要在报文中包含 `sharedSecret` 字段。

图标请求/响应明文 JSON（加密前）支持“单个”与“批量”两种形式，需向后兼容：

- 单个请求：
  ```json
  { "type": "ICON_REQUEST", "packageName": "com.example.app", "time": 1690000000000 }
  ```
- 批量请求：
  ```json
  { "type": "ICON_REQUEST", "packageNames": ["com.a","com.b", "com.c"], "time": 1690000000000 }
  ```

- 单个响应：
  ```json
  { "type": "ICON_RESPONSE", "packageName": "com.example.app", "iconData": "<base64(PNG)">, "time": 1690000000000 }
  ```
- 批量响应：
  ```json
  { "type": "ICON_RESPONSE", "icons": [
      { "packageName": "com.a", "iconData": "<base64(PNG)"> },
      { "packageName": "com.b", "iconData": "<base64(PNG)"> }
    ], "time": 1690000000000 }
  ```

注意：
- `iconData` 为 PNG 二进制的 Base64(NO_WRAP)。
- 建议单次批量请求的包名数量做合理限制（例如 50 个以内）以避免超大载荷；若需要更多，请分页多次请求。

---

### 2.3 应用列表同步（新）

- 请求：`DATA_APP_LIST_REQUEST:{uuid}:{publicKey}:{encryptedPayload}\n`
- 响应：`DATA_APP_LIST_RESPONSE:{uuid}:{publicKey}:{encryptedPayload}\n`
- 触发规范：
  - 由需要拉取对端“用户应用列表”的一方主动发起 `DATA_APP_LIST_REQUEST`（PC 或移动端均可）。
  - 接收方解密后若 `type=APP_LIST_REQUEST`，应采集本机“用户应用”（不含系统/预装/自身应用）并返回 `APP_LIST_RESPONSE`。
  - 可在认证完成后按需主动触发，或在 UI 上提供“同步应用列表”按钮触发。

明文 JSON 结构（加密前）：

- 请求：
  ```json
  { "type": "APP_LIST_REQUEST", "scope": "user", "time": 1690000000000 }
  ```
  - `scope`: 目前固定 `"user"`，表示仅用户安装应用（不含系统/预装/自身）。后续可扩展 `"all"`。

- 响应：
  ```json
  {
    "type": "APP_LIST_RESPONSE",
    "scope": "user",
    "apps": [
      { "packageName": "com.example.app1", "appName": "示例App1" },
      { "packageName": "com.example.app2", "appName": "示例App2" }
    ],
    "total": 2,
    "time": 1690000000000
  }
  ```

实现建议：
- 大列表时可考虑拆包分页（例如新增 `page`、`pageSize`、`hasMore` 字段）。当前实现为一次性返回。
- PC 端收到后可缓存并用于图标批量请求或联动映射。

---

## 3. 设备信息缓存与状态同步

- 需缓存设备 `uuid`、`displayName`、`ip`、`port`、`publicKey`、本地派生并安全存储的 `sharedSecret`、认证状态、最后在线时间等。
  - 存储建议（Android）：使用 `EncryptedSharedPreferences` 或 AndroidX Security 库进行加密存储。PC 端应尽量将 `sharedSecret` 存储到受保护的存储区（Windows Credential Manager、DPAPI、或使用应用的数据加密）。

---

## 4. 密钥派生与加密约定（实现互操作性要点）

- HKDF 参数：
  - HKDF‑Extract(salt, IKM)：salt = 32 字节零值（若无法提供 salt，可使用全 0 salt 与实现保持一致），IKM = UTF‑8(localPublicKey || remotePublicKey)（按字典序拼接保证双方一致）
  - HKDF‑Expand(PRK, info, L)：info = UTF‑8("shared-secret"), L = 32
  - 最终 `sharedSecret` = Base64( OKM )，OKM 为 32 字节

- AES‑GCM 使用：
  - Key = Base64 解码的 `sharedSecret`（32 字节）
  - IV = 12 字节随机
  - Tag = 16 字节（128 位）
  - 输出字节序：`IV || Ciphertext || Tag`，整体 Base64 编码作为报文中的 `encryptedPayload`

注意：所有实现必须在 HKDF 的 `info`、salt、IV/tag 布局和 Base64 编码细节上保持一致，才能保证互操作性。
- 设备状态流需支持“在线/离线”判定，未认证设备超时自动移除。