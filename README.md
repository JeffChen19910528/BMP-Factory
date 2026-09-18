# BPM Platform

一個真正的企業級 Business Process Management (BPM) 平台 —— 以設定驅動 (configuration-driven) 的
Workflow Engine 為核心，而不是把「表單 + 審核」硬寫死在程式碼裡的應用程式。

流程定義 (Process Definition) → 流程版本 (Process Version) → Workflow Engine → 執行實例
(Process Instance) 是完整分離的兩個模型：定義面永遠不可變 (immutable)，一個已經在跑的流程實例會
永遠釘選 (pin) 在它啟動當下的那個版本，不會因為之後重新發布而改變行為。

## 一、專案簡介

BPM Platform 提供從流程設計、表單設計、審核、SLA、通知、監控、報表、分析，到流程治理與系統管理
的完整功能，並且是一個 **.NET 10 Modular Monolith**（`BPM.Api` / `BPM.Application` /
`BPM.Domain` / `BPM.Infrastructure` / `BPM.Workflow` / `BPM.Identity` / `BPM.Notification`），
不是 microservices，也沒有 CQRS / Event Bus / Kafka / RabbitMQ 這類重量級基礎設施 —— 這是刻意的
架構決策，不是尚未完成。

前端是 React + TypeScript + Vite + Ant Design + React Flow，提供視覺化的流程設計器與表單設計器，
以及完整的執行期（Runtime）操作介面。

## 二、核心功能

- **Workflow Engine**：以 JSON 定義的流程圖（Start / UserTask / ApprovalTask / End），伺服端
  fail-closed 驗證，發布後永久不可變。
- **Form Engine**：12 種欄位類型、條件式 Visibility / Enabled / Required 規則、安全的宣告式
  Calculated 欄位公式、MinIO 附件上傳。
- **Approval Engine**：Sequential / All / AnyOne 三種審核政策，支援
  Approve / Reject / Return / Delegate / Transfer / Add Approver。
- **SLA 與通知**：每個任務自動計算 Warning / Due 時間，逾期會自動觸發 Warning / Overdue /
  Escalation 通知；站內通知與非同步 Email 通知兩種管道。
- **Dashboard / Monitoring / Reporting / Analytics**：即時營運總覽、流程實例監控、報表匯出
  （CSV）、歷史趨勢與流程比較分析 —— 全部基於同一套已授權範圍運算，絕不在瀏覽器端計算或洩漏未授權
  的資料。
- **Process Governance**：流程定義的 Suspend / Archive / Restore 生命週期、負責人 (Owner) 指派、
  版本比較。
- **System Administration**：使用者、組織、部門、角色、SLA 政策、稽核紀錄 (Audit Log)、系統健康
  狀態的完整管理介面。
- **雙語系 UI**：繁體中文 (`zh-TW`) 與 English (`en-US`) 即時切換（見第二十二節）。

## 三、系統架構

```
Web Frontend → REST API Layer → Application Layer
  (Process / Workflow / Task / Form / Approval / Notification / Audit Services)
  → Workflow Engine (State Machine / Transition & Condition Engine / Assignment / SLA)
  → Data Layer (PostgreSQL / MinIO)
```

- **Definition 與 Runtime 分離**：`ProcessDefinition` / `ProcessVersion` 屬於定義面；
  `ProcessInstance` / `TaskInstance` / `Approval` 屬於執行面。
- **流程圖以 JSON 儲存**（`ProcessVersion.DefinitionJson`），不是拆成 Node / Transition 資料表 ——
  一個已發布版本就是一個不可變的 JSON blob，避免「不可變」的定義隨著資料表增生而失守。
- **Condition 是資料，不是程式碼**：條件式路由以固定 operator 集合的 JSON 表示
  (`==, !=, >, >=, <, <=, IN, NOT_IN, CONTAINS, IS_NULL, IS_NOT_NULL`)，永遠不會在伺服端 `eval`
  任意程式碼。
- **Optimistic Concurrency（RowVersion）** 保護每一個「讀取 → 編輯 → 儲存」端點，避免兩個使用者
  同時修改同一筆資料時互相覆蓋。
- **Mutating 端點具備 Idempotency** —— 例如同一個 Business Key 重複啟動流程會被拒絕
  （`409 PROCESS_INSTANCE_DUPLICATE_BUSINESS_KEY`），而不是建立兩筆重複的流程實例。
- **Audit Log 不可竄改**：所有重要操作（登入、流程 CRUD / 發布、任務指派 / 審核、權限變更）都會被
  記錄，一般使用者無法修改或刪除。

## 四、技術架構

**後端**：.NET 10 / ASP.NET Core Web API、EF Core + Npgsql、JWT Bearer 驗證、Serilog、
FluentValidation、Swashbuckle/Swagger。

**前端**：React 19、TypeScript、Vite、Ant Design 6、React Flow（流程/表單設計器畫布）、
TanStack Query（資料快取）、Zustand（本地狀態）、Axios。

**基礎設施**：Docker Compose（`postgres`、`redis`、`minio`、`bpm-api`、`bpm-web`）。
**Redis 目前未被應用程式實際使用**（`docker-compose.yml` 有這個服務，但程式碼中完全沒有
`IConnectionMultiplexer` 或任何 Redis client 呼叫）—— 這是已知、刻意保留的現況，不是待修的缺陷，
請見第二十四節。

## 五、專案目錄

```
src/
  BPM.Api/            REST API、Controllers、中介軟體
  BPM.Application/    DTO、介面、跨模組共用邏輯
  BPM.Domain/         Entity、Enum、純領域模型
  BPM.Infrastructure/ EF Core、Persistence、大部分 CRUD-style Service
  BPM.Workflow/        Workflow / Approval / Form / SLA 的實際引擎邏輯（internal，不對外暴露）
  BPM.Identity/        登入、JWT 簽發
  BPM.Notification/    Email 非同步遞送 worker
  BPM.Tests/            單元測試與整合測試（需要真實 PostgreSQL）

frontend/
  src/components/      共用元件（AppLayout、QueryStateView、ApiErrorAlert …）
  src/features/        依領域切分的頁面（process / form / task / approval / administration …）
  src/i18n/             雙語系翻譯字典與 LanguageProvider（見第二十二節）
  src/services/         API client
  src/stores/            Zustand store（目前只有登入狀態）
```

## 六、快速開始

前置需求：.NET 10 SDK、Docker Desktop、Node.js（開發前端需要）。

```bash
# 從 repo 根目錄啟動 postgres / redis / minio / bpm-api
JWT_SECRET="<至少 32 字元的真實密鑰>" docker compose up -d postgres redis minio bpm-api
```

API 啟動時會自動套用 EF Core migrations，並建立一個 `Administrator` 角色與
`admin` / `ChangeMe123!` 的預設帳號 —— **這組密碼在正式環境必須立即更換**。API 預設監聽
`http://localhost:5080`，Swagger UI 在 Development 環境下位於
`http://localhost:5080/swagger`。

## 七、Docker 啟動

```bash
JWT_SECRET="<至少 32 字元的真實密鑰>" docker compose up -d
```

會啟動全部 5 個服務：`postgres`、`redis`、`minio`、`bpm-api`、`bpm-web`。`bpm-web` 是一個
multi-stage build 的 nginx image，執行期透過 `docker-entrypoint.sh` 寫入 `env-config.js` 來讀取
`VITE_API_BASE_URL`（Vite 原本會在 build time 就把 `VITE_*` 變數寫死，這個 image 特別處理過，讓
同一個 image 能在不同環境下重複使用）。

停止服務但保留資料：`docker compose down`。**不要**對正式資料執行
`docker compose down -v`（會刪除 volume，也就是資料庫與檔案資料）。

## 八、Development（本機開發）

只跑 API（不用 Docker 跑 API 本身）：

```bash
docker compose up -d postgres redis minio
cd src
dotnet run --project BPM.Api
```

前端開發伺服器：

```bash
cd frontend
npm install
cp .env.example .env.local   # VITE_API_BASE_URL=http://localhost:5288
npm run dev                  # 預設在 http://localhost:5173
```

`docker compose up -d bpm-web` 也可以直接跑前端（見第七節）。

## 九、Production Deployment

**目前唯一提交到 repo 的 `docker-compose.yml` 將 `ASPNETCORE_ENVIRONMENT` 寫死為
`Development`，且沒有 `appsettings.Production.json`。** 這代表：如果原封不動照著本文件的指令
部署到正式環境，Swagger UI 會在沒有驗證的情況下對外公開 —— **正式部署前必須明確地把
`ASPNETCORE_ENVIRONMENT` 覆蓋為 `Production`**（例如透過 `docker-compose.override.yml` 或部署
平台自己的環境變數機制），並視需要提供對應的 `appsettings.Production.json`。這是 Phase 11
Architecture Discovery 找出、目前仍待部署方自行處理的真實落差，詳見 `DEPLOYMENT.md`。

其他部署前必須確認的項目（完整清單見 `DEPLOYMENT.md`）：

- `JWT_SECRET`：至少 32 字元，且不可寫在版本控制中；`docker-compose.yml` 若未設定會直接
  fail-fast，不會用不安全的預設值啟動。
- `Cors:AllowedOrigins`：需要依實際網域設定，目前沒有文件化的預設值。
- 反向代理（reverse proxy）情境下，需確認 `UseForwardedHeaders()` 的設定是否符合實際部署拓樸。
- 目前 5 個 Docker 服務**都沒有設定 `restart:` policy** —— 若 PostgreSQL 短暫無法連線導致
  `bpm-api` 在啟動時崩潰，不會自動重啟。正式部署建議自行加上合適的 restart policy。

## 十、Authentication / Authorization

JWT Bearer 驗證，登入端點會核發 access token。後端永遠不信任前端傳來的身份資訊 ——
`UserId`、`RoleId`、`DepartmentId`、任何狀態欄位都以伺服端解析出的身份為準，不會讀取 request
body 裡宣稱的身份。

前端隱藏某個選單或按鈕只是 UX，不是安全邊界 —— 真正的授權判斷永遠在後端（例如
`[Authorize(Roles = "Administrator")]`，或針對特定資源的擁有者/參與者判斷）。每一個涉及
User / Organization / Department / Role / Process / Task / Form Instance / Attachment /
Notification / Audit 的端點都會確認呼叫者是否真的有權限存取「這一筆」資源，不能單純靠猜測 ID
就存取到別人的資料 (IDOR)。

## 十一、Workflow

流程以 `WorkflowDefinition` JSON 表示：`nodes` 與 `transitions`。發布前會經過完整的驗證
（有且僅有一個 Start、至少一個 End、沒有孤立節點、所有節點可達、沒有非法 transition、沒有缺少
assignee/form、沒有非法 condition、沒有重複 node id）—— 任何一項不通過都會擋下發布。目前引擎只
支援**循序流程**（Start → UserTask* → End），尚未支援 Gateway / Timer / SubProcess。

## 十二、Form

`FormDefinition` / `FormVersion` / `FormInstance`，12 種欄位類型，支援條件式 Visibility /
Enabled / Required 規則、AND/OR/NOT 複合條件，以及安全的宣告式 Calculated 欄位公式（僅支援數字
常數、欄位參照、`+ - * /` 與括號，沒有函式呼叫，也沒有任何形式的 `eval`）。一個 UserTask 綁定的
`FormVersion` 會在流程發布當下被釘選，之後即使表單被重新發布，已經在跑的流程實例仍會沿用原本的
表單版本。

## 十三、Approval

`ApprovalTask` 節點支援 Sequential（依序）、All（全體）、AnyOne（任一人）三種政策，動作包含
Approve / Reject / Return / Delegate / Transfer / Add Approver，全部由後端權威判定，並以
RowVersion 保護並行操作的正確性。Return（退回上一個節點，流程繼續跑）與 Reject（流程終止）是
兩個語意完全不同的動作。

## 十四、SLA

`SlaPolicy`（管理員設定）→ `TaskSla`（每個任務的執行紀錄）。任務建立時自動計算 Warning / Due
時間；一個獨立的 `SlaSchedulerWorker` 會定期檢查並將逾期任務標記為 Overdue，並觸發對應通知，全部
以資料庫層級的 conditional update 保證多個 worker 並行執行時不會重複通知。

## 十五、Notification

站內通知（`Notification` 實體，含未讀計數與通知鈴鐺 UI）與非同步 Email 通知
（`NotificationDelivery` + 一個獨立的背景 worker）兩個管道。Workflow / Approval /
SLA 引擎完全不知道 Email 是否啟用 —— 它們只負責寫入一筆通知紀錄，真正是否寄送 Email 由
`EmailDeliveryWorker` 自行決定。目前 Docker 開發環境使用假的 Email provider（`Email:Provider=Fake`），
正式環境需要另外設定真實的 SMTP。

## 十六、Monitoring / Reporting / Analytics

- **Dashboard**：個人化的「我的任務 / 待審核 / SLA 總覽 / 流程總覽 / 最近活動」。
- **Process Monitoring**：所有流程實例的即時清單，支援搜尋、篩選、排序、分頁。
- **Reporting**：Process / Task / Approval / SLA 的彙總報表，以及可篩選的明細表與 CSV 匯出
  （匯出已防範 CSV 公式注入攻擊）。
- **Analytics**：流程量／完成／駁回趨勢、耗時分析、節點分析、SLA 達成率趨勢、流程比較 ——
  全部是描述性的歷史統計，沒有預測、沒有機器學習。

以上全部從同一套已授權範圍（一般使用者只看得到自己相關的資料，Administrator 看得到全系統）計算，
彙總永遠發生在授權過濾**之後**，不會有使用者能透過報表或分析頁面看到超出自己權限的資料。

## 十七、Process Governance

Administrator 或流程的 Owner 可以 Suspend（暫停，阻擋新的流程啟動，不影響已在跑的實例）、
Archive（封存，不代表刪除任何資料）、Restore（還原為 Published）一個流程定義，並可以比較任意兩個
版本之間的結構差異。生命週期狀態的改變**不會**回溯影響任何已經在執行中的流程實例、任務、審核或
SLA。

## 十八、Administration

Administrator-only 的管理介面：使用者、組織、部門、角色、SLA 政策、稽核紀錄、系統健康狀態
（PostgreSQL / MinIO 連線狀態、通知遞送積壓、Redis 誠實回報為「未串接」、以及少量非機密設定值 ——
絕不回傳任何密碼或連線字串）。移除一個 Administrator 自己的 Administrator 角色會被明確擋下，避免
系統管理員把自己鎖在系統外面。

## 十九、Audit

所有重要操作都會寫入 `AuditLog`：誰、在什麼時間、對什麼資源、做了什麼動作。一般使用者無法修改或
刪除稽核紀錄，只有 Administrator 能查詢。

## 二十、Attachment / MinIO

檔案中繼資料存在 PostgreSQL，實際檔案內容存在 MinIO（S3 相容的物件儲存）。上傳有內容類型／副檔名
白名單限制；上傳、下載、刪除任一環節失敗都會回傳清楚的錯誤，而不是讓原始例外訊息外洩，也不會留下
沒有對應資料庫紀錄的孤兒檔案。

## 二十一、Backup / Restore

`DEPLOYMENT.md` 有完整的備份／還原操作程序（`pg_dump` / `pg_restore` 搭配 `mc mirror`），並
說明為什麼 PostgreSQL 與 MinIO 的備份必須成對進行 —— 一筆 `Attachment` 紀錄只有搭配它在 MinIO
裡對應的檔案物件才有意義。**這是操作程序文件，不是自動化備份服務或排程器** —— 目前系統本身不會
自動執行備份。

## 二十二、語系切換（繁體中文 / English）

系統支援繁體中文 (`zh-TW`) 與 English (`en-US`) 兩種語言，切換入口在畫面右上角 Header（地球圖示
旁邊，使用者選單左側）。

- **預設語言是 `zh-TW`。**
- 切換語言會**立即**更新目前頁面上的翻譯文字，不需要重新整理，也不需要重新登入。
- 選擇的語言會存在瀏覽器 `localStorage`（key 為 `bpm-language`），下次開啟時會記住上次的選擇。
  這純粹是前端顯示偏好，**不會**寫入資料庫，也不會影響任何後端行為或 API 回應內容。
- Ant Design 元件本身的文字（例如 DatePicker 的「今天」按鈕、Table 的分頁文字）也會跟著切換的
  語言同步變化。
- 錯誤訊息：後端的 `{code, message, traceId}` 錯誤格式完全不受語系影響 —— 前端會依 `code` 對應
  一段本地化文字；如果某個錯誤代碼還沒有對應的翻譯，會直接顯示後端原本的英文 `message`，而不是
  顯示空白或 `undefined`。

**翻譯涵蓋範圍**：`frontend/src/{components,pages,features}` 底下所有具備使用者可見文字的
`.tsx` 元件（65 個）都已改為透過 `useTranslation()` 取字串，包含 Process Designer / Form
Designer 畫布內部、Administration 全部子頁面與其 Create/Edit Modal、Approval / Task Detail 的
Approve / Reject / Return / Delegate / Transfer / Add-Approver 全部動作、Form Runtime 等 ——
只有 2 個元件（`ComingSoon.tsx`、`ProtectedRoute.tsx`）刻意保留不變，因為它們本身沒有寫死的文字
（標題／說明由呼叫端以 props 傳入，或純粹是導頁邏輯）。語系切換的核心機制（切換、持久化、AntD
locale 同步、缺字 fallback）與內容覆蓋範圍均已完整實作並有自動化測試涵蓋，詳見
`docs/PHASE_13_RELEASE_READINESS.md`。

## 二十三、Testing

```bash
# 後端
cd src
dotnet test                     # 需要本機 PostgreSQL 監聽 localhost:5432
                                 # （測試會自行建立/遷移一個獨立的 "bpm_test" 資料庫）

# 前端
cd frontend
npm test                        # 單元測試（Vitest + React Testing Library），不需要後端
npm run test:live               # 對真實執行中的 bpm-api 做端對端驗證
```

目前基準（詳見 `CHANGELOG.md` 與 `docs/`）：Backend 492+、Frontend 374+（含新增的語系測試）、
Live 42+，全部曾連續執行 3 次確認結果穩定。

## 二十四、Known Limitations（已知限制，非 Release Blocker）

以下是目前系統刻意保留、經過評估後判斷「不是」正式發布阻礙的現況：

- **Browser 自動化驗證不可用** —— 見第二十五節。
- **Redis 完全未被使用**：`docker-compose.yml` 裡有這個服務，但應用程式沒有任何程式碼實際連線
  使用它。
- **沒有 High Availability**：目前是單一 API 實例、單一資料庫實例的部署模型。
- **沒有 CI/CD pipeline**。
- **沒有 MFA（多因子驗證）**。
- **沒有 Token 撤銷機制**：JWT 簽發後在到期前無法被主動撤銷。
- **沒有 `ProcessPermission` 細粒度權限系統**：目前的權限模型是角色（Role）加上少數資源擁有者
  （Owner）判斷，沒有「誰可以設計 / 誰可以發布 / 誰可以啟動」這種更細的權限。
- **沒有對外的 External API / Webhook / Import-Export 整合能力**。
- **沒有 keyset pagination**：目前所有分頁都是 offset-based（`Skip`/`Take`），大頁碼下已加上
  安全的溢位保護，但效能特性仍是 offset pagination 的特性。
- **沒有 `pg_trgm` 模糊搜尋索引**：少數幾個 `ILike` 前導萬用字元搜尋目前沒有對應索引，在目前的
  資料量下尚未測得需要處理的效能問題。

## 二十五、Browser Verification 限制

本專案在開發過程中多次嘗試使用 Claude-in-Chrome 瀏覽器自動化工具做端對端的 UI 驗證，但該擴充
功能在目前的開發環境中始終未連線。**因此所有「畫面實際看起來如何」的驗證，都是透過 Backend
測試、Frontend 單元測試、對真實執行中 API 的 Live HTTP 測試、以及直接檢查 Docker 容器狀態來完成
的，不曾假裝執行過瀏覽器測試。** 如果之後在有瀏覽器自動化工具可用的環境中重新驗證，建議依照
`docs/PHASE_13_RELEASE_READINESS.md` 第 Q 節列出的使用者旅程逐一走過一次。

## 二十六、License / Project Information

本專案以 AI 協作方式開發；完整的規格文件、架構決策紀錄、以及逐 Phase 的開發筆記保存在本機
（已加入 `.gitignore`，不隨此 repo 一併公開）。`README.md`（本文件）與 `CHANGELOG.md` 是公開、
獨立可讀的文件，不依賴前述本機筆記也能理解如何啟動、部署與使用本系統。

## 文件索引

- `README.md`（本文件）—— 專案總覽、快速開始、各功能模組簡介。
- `CHANGELOG.md` —— 逐版本的異動紀錄（英文）。
- `DEPLOYMENT.md` —— 部署設定、健康檢查、備份／還原程序（英文）。
- `docs/PHASE_12_PERFORMANCE_DISCOVERY.md` —— 效能調查與優化紀錄（英文）。
- `docs/PHASE_13_RELEASE_READINESS.md` —— Release Candidate 驗收檢查清單（英文）。
