# PurchaseFlow — REST API 契約 v1.0.1

> 狀態：**已確認核心契約（文件釐清 v1.0.1）**｜2026-10-23。與 [`MVP.md`](MVP.md) 的角色、流程及資料規則同步。

## 1. API 契約

所有端點使用 `/api` 前綴，JSON 使用 camelCase。建議使用 Controllers、DTO、ASP.NET Core ProblemDetails／ValidationProblemDetails。

### 1.1 認證

| Method | 路徑 | 用途 |
|---|---|---|
| GET | `/api/auth/csrf` | 提供同源 Cookie 登入／寫入所需 CSRF token |
| POST | `/api/auth/login` | 帳密登入 |
| GET | `/api/auth/me` | 目前登入者及角色 |
| POST | `/api/auth/logout` | 登出 |

**已確認採用 ASP.NET Core Identity + HttpOnly 認證 Cookie + CSRF 防護**。正式部署時 Vue 前端與 API 同源；開發階段透過 Vite proxy 轉發 `/api`。Cookie 登入 API 將由程式自行封裝 Identity SignInManager，而非直接暴露包含公開註冊的所有內建 Identity 端點。這裡不是自行存放／雜湊密碼。登入成功後需更新 CSRF token；保護所有改變狀態的 API（包括登入、登出、PUT、POST），前端在請求標頭攜帶 token。

### 1.2 採購申請

| Method | 路徑 | 使用者與條件 | 結果 |
|---|---|---|---|
| GET | `/api/purchase-requests?view=mine` | 員工 | 自己的申請列表 |
| GET | `/api/purchase-requests?view=pending` | 主管 | 全部待審核列表 |
| GET | `/api/purchase-requests?view=reviewed` | 主管 | 全部已審核列表 |
| POST | `/api/purchase-requests` | 員工 | 建立 Draft，`201` |
| GET | `/api/purchase-requests/{id}` | 本人或主管可見的送審資料 | 單筆明細及審核紀錄（如有） |
| PUT | `/api/purchase-requests/{id}` | 員工且本人 Draft | 覆蓋草稿欄位與品項 |
| POST | `/api/purchase-requests/{id}/submit` | 員工且本人 Draft | Draft → Pending |
| POST | `/api/purchase-requests/{id}/decision` | 主管且 Pending | Pending → Approved／Rejected |

### 1.3 DTO 範例（示意，不是可直接複製的全部欄位）

建立草稿：

```json
{
  "title": "辦公室電腦採購",
  "purpose": "替換老舊設備",
  "items": [
    { "name": "筆記型電腦", "quantity": 2, "estimatedUnitPrice": 28000.00 }
  ]
}
```

成功建立回應：

```json
{
  "id": 1,
  "title": "辦公室電腦採購",
  "status": "Draft",
  "totalAmount": 56000.00,
  "rowVersion": "AAAAAAAAAAE=",
  "items": [
    { "id": 1, "name": "筆記型電腦", "quantity": 2, "estimatedUnitPrice": 28000.00, "lineTotal": 56000.00 }
  ],
  "approvalLog": null
}
```

**`totalAmount` 和 `lineTotal` 是後端以資料庫品項資料計算的 API 回應欄位，並非 SQL Server 資料表欄位；`rowVersion` 則對應主單實體欄位。** 詳見 [`MVP.md` §4.2](MVP.md)。

`rowVersion` 為示意 Base64 值。修改草稿、送審與審核都**必須**攜帶從上次 GET／成功更新取得的 `rowVersion`。後端不可只讀取當次 EF Core 查詢到的新版本而忽略客戶端版本，須以客戶端版本作為並行控制基準（先比對並設定 OriginalValue，或採等效的條件式更新），避免舊畫面覆寫新資料。每次成功修改回傳新的版本。

主管審核：

```json
{
  "decision": "Rejected",
  "comment": "請補充用途與規格",
  "rowVersion": "AAAAAAAAAAI="
}
```

決策成功回 `200` 與更新後明細（包含唯一的 approvalLog）；重複、舊版本、非 Pending 回 `409`。提交、更新與審核不可允許前端指定 `requesterId`、`reviewerId`、審核時間或總金額。

### 1.4 錯誤回應原則

| HTTP | 適用情況 |
|---|---|
| `400` | 格式不合法、欄位驗證失敗（ProblemDetails／ValidationProblemDetails） |
| `401` | 尚未登入或登入失效 |
| `403` | 已登入但角色不允許該動作 |
| `404` | 單據不存在或不得查看其存在性 |
| `409` | 狀態不允許轉換、已被其他操作改變、重複審核 |
| `201` | 草稿建立成功 |
| `200` | 查詢或更新成功 |
| `204` | 登出成功 |

Cookie CSRF 驗證失敗要阻擋寫入（統一回 `400` 或 `403`，在實作時固定並測試）；Cookie 登入的 API 未授權回應應為 `401`／`403`，不應以 302 導去 HTML 登入頁；不可把伺服器內部例外堆疊回傳給使用者。

## 2. 交易與一致性契約

- `PUT /api/purchase-requests/{id}` 以完整品項清單覆蓋草稿品項；此端點不開放變更申請人、狀態、總額、審核者。即使只變更品項也必須更新主單版本。
- `POST /submit` 必須檢查 Draft、所有權、有效品項以及客戶端 `rowVersion`，成功後更新 Pending 與版本。
- `POST /decision` 必須檢查 Manager、Pending、備註驗證與客戶端版本；**主單狀態更新與一筆 ApprovalLogs INSERT 在同一個交易內提交**。優先使用單次 `SaveChangesAsync` 的預設交易。
- EF Core concurrency exception，以及專屬審核紀錄唯一索引違反所代表的競態，轉成 `409`；其他資料庫錯誤不可全部當成 409。
- 前端收到 409 顯示衝突提示並重新取得伺服器資料，不自動重送審核。

## 3. 最低必要驗收

- 401／403／404／409 對應的角色、資源及狀態測試。
- 過期 `rowVersion` 的草稿修改、送審與審核不得成功。
- 重複與同時審核只成功一次，主單狀態與 ApprovalLogs 始終一致。
- 所有已登入後發起的狀態變更 API 都必須通過 CSRF 驗證；缺少／錯誤 token 被拒。
