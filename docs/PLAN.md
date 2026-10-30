# PurchaseFlow — 開發計畫與決策紀錄

> 狀態：**階段 0～5 完成，準備進入階段 6 Vue 前端實作**｜2026-10-30。

## 1. 程式架構（精簡原則）

```text
PurchaseFlow/
├─ frontend/                 # Vue 3 + TypeScript + Vite
├─ backend/
│  └─ PurchaseFlow.Api/      # ASP.NET Core Web API、Identity、EF Core、Migrations
├─ tests/
│  └─ PurchaseFlow.Api.Tests/ # xUnit 單元／整合測試
├─ docs/
│  ├─ MVP.md                 # 本文件確認後的目標、需求、權限、狀態、ERD、頁面
│  ├─ API.md                 # API Request／Response、錯誤與驗證
│  └─ PLAN.md                # 階段、驗收、決策紀錄
└─ README.md                 # 目的、技術、設定、啟動、測試、Demo
```

- API 採 Controllers + DTO + EF Core DbContext；把具有交易及狀態控制的操作放進簡單的 `PurchaseRequestService`（或同等明確職責的類別）。
- 不引入 Generic Repository、Unit of Work 包裝、CQRS、Clean Architecture 多專案分層或訊息佇列。
- EF Core `SaveChanges` 的單次呼叫會在支援的資料庫下使用交易；讓狀態更新與新增審核紀錄在**同一個 SaveChanges** 內。只有真的跨多次 SaveChanges 時才評估明確開交易。
- 對 `DbUpdateConcurrencyException` 回 `409`；配合審核紀錄唯一約束保障競態情境。
- .NET 10 使用官方 OpenAPI 文件生成。互動式 Swagger UI／Scalar 不隨內建 OpenAPI 自動提供，只有需要時再挑一個加上。

## 2. 開發階段與完成標準

| 階段 | 工作 | 驗收標準 |
|---|---|---|
| 0. 規格定案 | 角色、審核規則、資料表、API、畫面、開發環境 | 文件無互相矛盾的權限、狀態與欄位；待確認事項已決定 |
| 1. 環境與骨架 | Git、.NET 10、API、EF Core、SQL Server、Vue 專案、初始 Migration、OpenAPI | API／前端可啟動；資料庫可重建；設定未洩露機密 |
| 2. 認證授權 | Identity、角色、測試帳號、登入／登出、CSRF、授權政策 | 員工／主管各自可登入，未授權 API 被拒絕；不公開註冊 |
| 3. 草稿與品項 | 主單／明細的新增、列表、明細、修改、金額計算與驗證 | 草稿可持久儲存、金額正確、無法讀改他人草稿 |
| 4. 送審流程 | Draft → Pending、欄位驗證、版本檢查 | 空品項無法送審；送出後不得修改；重送回 409 |
| 5. 主管審核 | 待審核清單、核准／拒絕、Audit Log、交易、競態處理 | 僅 Pending 可審；拒絕理由必填；同筆最多一筆 Log；同時審核僅一筆成功 |
| 6. Vue 畫面與整合 | 四頁模板、角色導航、API 串接、錯誤狀態 | 員工與主管可在瀏覽器操作完整情境，且不可越權 |
| 7. 完整驗收與展示 | xUnit 補齊測試、手動驗收、README、部署或 Demo 錄影 | 測試通過；乾淨環境可建置；作品有可驗證的展示入口與啟動說明 |

**進度摘要：**

- 階段 0～5：已完成。
- 階段 5：主管清單、核准／拒絕、審核紀錄、RowVersion、並行審核與交易一致性。
- 截至本階段，24 項 xUnit 測試通過。
- 階段 6～7：尚未完成。

**測試不要全部延到階段 7。** 每個後端功能階段都應同步加入關鍵測試；階段 7 是補齊與總驗收。

### 2.1 最少必測情境

- 未登入不能存取保護 API；員工不能審核，主管不能修改員工草稿。
- 員工 A 不能查看／修改員工 B 的申請。
- 建立與修改合法草稿；金額 = 每項數量 × 單價之和；拒絕負數、零與無效輸入。
- 空品項不能送審；Pending 不允許再修改／重送。
- 主管核准；主管拒絕時記錄理由、審核者與時間。
- 重複審核必須衝突，不能產生第二筆審核紀錄。
- 同時兩個主管審核同一單時，只有一筆成功且狀態／審核紀錄保持一致。
- 審核紀錄寫入失敗時狀態不得單獨更新。
- 用 SQL Server 進行與 `rowversion`／FK／唯一索引有關的整合測試；不要僅靠 EF Core InMemory 代替這些 SQL Server 行為測試。

## 3. 決策紀錄

| 決策 | 狀態 | 採用方案 |
|---|---|---|
| D1 角色 | 已確認 | Employee／Manager，一帳號單角色 |
| D2 登入 | 已確認 | Identity Cookie、CSRF、同源部署、測試帳號預建、無公開註冊 |
| D3 草稿 | 已確認 | 可空草稿，送審至少一品項，無申請刪除 API |
| D4 審核 | 已確認 | 拒絕理由必填，核准備註選填，一次最終決策 |
| D5 金額 | 已確認 | TWD、`decimal(18,2)`，由後端計算 |
| D6 並行 | 已確認 | SQL Server rowversion、HTTP 409、審核紀錄唯一索引、原子提交 |
| D7 展示／部署 | 後期決定 | 階段 1 先確保 Windows 本機開發可用，後期評估 IIS／雲端／影片 |

階段 0 完成依據：D1～D6 已由使用者確認，核心角色、狀態、ERD、API 契約與最低測試均已整理。D7 不妨礙後端實作。

## 4. 下一步：階段 6 Vue 前端實作

1. 串接登入／登出與 Cookie／CSRF，依員工／主管角色提供導航。
2. 實作申請清單與草稿編輯，串接品項、金額顯示及送審流程。
3. 實作主管待審核清單、核准／拒絕操作與審核紀錄顯示。
4. 整合 API 錯誤處理，呈現欄位驗證、未登入、權限不足及 RowVersion 衝突等訊息。
5. 驗收：員工與主管可在瀏覽器完成各自流程，且不可越權。

### 4.1 階段 1 環境與專案骨架（歷史規劃）

1. **先檢查本機安裝狀況**：`.NET 10 SDK`、Git、Node／npm、SQL Server（Developer 或 Express／LocalDB，選一個，優先避免重複安裝）；已有的 Vue／Node 環境可沿用。
2. 確認資料庫服務可連線，再建立 `backend/PurchaseFlow.Api`、`backend/PurchaseFlow.Tests`、`frontend` 和 Git 儲存庫；不提前開發業務功能。
3. 建立 ASP.NET Core Web API Controllers + OpenAPI，EF Core SQL Server provider，Identity 基本 Schema、初始 Migration（是否先建業務 Schema 以階段 1 實際檢查為準）。
4. 前端建立 Vue 3 + TS + Vite 可啟動骨架，配置 `/api` 代理（完整 Cookie／CSRF 整合於階段 2 驗收）。
5. 驗收：API 可執行、OpenAPI JSON 可讀、SQL Server 連線可用、Migration 能重建 DB、前端可啟動、敏感連線密碼不進 Git、測試專案可執行。

**本機前置檢查指令（PowerShell）：**

```powershell
dotnet --info
dotnet --list-sdks
node --version
npm --version
git --version
# 若已安裝 SQL Server LocalDB，可再執行：
sqllocaldb info
```

上述為本機檢查指令，不代表目前已在使用者電腦執行過。階段 1 實作時以實際輸出選擇資料庫實例與開發設定。

## 5. 技術參考

- EF Core 樂觀並行與 rowversion：<https://learn.microsoft.com/en-us/ef/core/modeling/concurrency>
- ASP.NET Core Cookie 與 CSRF：<https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0>
- ASP.NET Core OpenAPI：<https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0>
- EF Core 資料庫測試：<https://learn.microsoft.com/en-us/ef/core/testing/testing-with-the-database>

相關規格：[`MVP.md`](MVP.md)、[`API.md`](API.md)。
