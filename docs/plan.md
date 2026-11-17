# unity_video_generation 製作計畫

> 2026-11-17 專案由 fable_video_generation 改名為 unity_video_generation；品牌為 Claude × Unity，不標版本，目標含直式（9:16）與橫式（16:9）。以下 30 秒片計畫為當時紀錄，30 秒片仍為直式；故事短片已支援直式與橫式（見文末）。

目標：30 秒直式短影片，介紹「用 Claude（製作當時為 Fable 5.2）搭配 Unity 做短影音並上傳 Reels」的流程。

## 規格
- 1080×1920、30 fps、30 秒、H.264 + AAC 48 kHz、響度 -14 LUFS
- 旁白：macOS `say` 台灣中文語音（目前 Meijia）
- 字幕：台灣正體中文為主、英文並列，放在畫面中段安全區
- 觸控視覺：沿用 mtk-viewer（黑底、色相依觸點、擴散圈半徑 40→120、0.35 秒淡出、線寬 4、圓點帶白色粗體編號）

## 流程
1. `script.json`：分鏡、時間、雙語台詞的唯一來源
2. `python3 scripts/build.py animatic`：旁白 + 字幕 + 分鏡示意卡，輸出 `output/animatic.mp4`
3. `scripts/unity_render.sh`：Unity 批次模式執行 `ReelBuilder.Render`，以程式建場景並逐格算圖，輸出 `output/unity_render.mp4`（無字幕、無聲）。加上時間參數（例如 `1.5,13,29`）只輸出靜態 PNG 供檢查
4. `python3 scripts/build.py final output/unity_render.mp4`：疊上旁白與雙語字幕，輸出 `output/final.mp4`
5. 上傳 Reels：由使用者登入並按下發布，音樂在 Reels 內挑選

## Unity 算圖做法
- 不用 Recorder、不進 Play 模式：每個動畫都是時間的純函數，逐格 `Camera.Render()` 後把像素直接送進 ffmpeg，結果可重現
- 3D 攝影機先畫到自己的 RenderTexture，再由 2D 正交攝影機當背景貼圖合成（兩台攝影機畫同一張 MSAA 貼圖時，第一層在 Metal 上會消失）
- 關閉 fog：批次算圖時線性霧會把整個 3D 層蓋成背景色
- 關閉非同步 shader 編譯，避免第一格缺物件
- 中文字用 `Font.CreateDynamicFontFromOSFont`（PingFang TC、Menlo），不需匯入字型資產
- 分鏡時間目前同時寫在 `script.json` 與 `ReelBuilder.cs` 的 `Cuts`，改時間要兩邊一起改

## 狀態（2026-11-12）
- 完成：全流程跑通，`output/final.mp4` 已產出（1080×1920、30 fps、30.00 秒、-14.5 LUFS）
- Unity 授權：舊授權綁在復原前的機器上，已改名為 `~/Library/Unity/licenses/UnityEntitlementLicense.xml.stale-20261112`，重開 Hub 後取得新授權
- 未做：配樂、git commit、GitHub 遠端、上傳

## 故事短片：星辰城的小星星（2026-11-13）
- 來源：`stories/star_city/story.json`（分鏡、雙語台詞）、`stories/star_city/refs/`（使用者提供的六張 1024×576 場景圖）
- 流程（2026-11-17 起一個指令）：`python3 scripts/story.py star_city`；分步驟為 `story.py star_city timeline` → `story.py star_city render -f <格式>`（內部呼叫 `scripts/story_render.sh star_city <格式>`）→ `story.py star_city final -f <格式>`。詳見下方「直式與橫式輸出」
- 時間軸由旁白音檔決定：每句各自合成、去頭尾靜音、依句間停頓排列，結果寫入 `output/star_city/timeline.json`（兩種格式共用），Unity 與字幕共用
- 旁白原文 59 秒放不下，精簡台詞後再整體加速 1.108 倍；上限設 1.15 倍，超過就要再刪字
- 版面（直式）：畫面佔上方 1340 px，下方深色面板放字幕。原因有二：圖只有 576 px 高，全幅放大 3.3 倍太糊，這樣只放大 2.3 倍；而且圖的前景地面在最下緣，全幅會被字幕與 Reels 介面蓋住
- 圖片先用 lanczos 放大兩倍並銳化（`refs_2x/`），再交給 Unity
- `StoryBuilder.cs`：每張圖是一個場景，鏡頭移動就是場景根節點的位移與縮放，角色與光效都用圖片像素座標定位，所以橫移時不會跑位
- 字幕相鄰兩句的時間不可重疊，否則 libass 會把字幕上下推開
- 動態字型的 TextMesh 要先呼叫 `RequestCharactersInTexture`，否則批次算圖會缺字

## 直式與橫式輸出（2026-11-17）
- 指令：`python3 scripts/story.py <name> [all|timeline|render|final] [-f vertical|horizontal|both] [--stills 3,20]`，預設 `all` 加 `both`，一次完成旁白、時間軸、兩種格式的 Unity 算圖、字幕燒入與合成（star_city 兩種格式約 83 秒，Mac Studio M4 Max）。`story_render.sh <name> <vertical|horizontal> [stills]` 由 story.py 呼叫，以 `STORY_FORMAT` 環境變數告訴 Unity 用哪種格式
- 共用時間軸：兩種格式用同一份旁白與 `timeline.json`，只有構圖與字幕版面不同，所以聲音、畫面節奏一致
- 輸出配置：共用 `output/<name>/narration.wav`、`timeline.json`；各格式在 `output/<name>/<format>/` 下有 `unity_render.mp4`、`subtitles.ass`、`final.mp4`、`still_<t>.png`
- 直式 1080×1920：畫面佔上方 1340 px，下方面板放字幕，版面不變
- 橫式 1920×1080：畫面鋪滿整個畫格。原圖是 16:9（1024×576），放大 1.875 倍剛好填滿，不會裁切或留黑邊；鋪滿後鏡頭平移沒有餘裕，所以額外加 ZK 1.12 的縮放讓平移有空間。標題放在 y=150
- 橫式字幕：直接疊在畫面上，中文以底部對齊在 y=960，英文掛在其下 y=976；畫面底部加一層漸層壓暗，確保字幕可讀
- 30 秒流程介紹（`script.json`、`scripts/build.py`、`scripts/unity_render.sh`、`ReelBuilder.cs`）仍只有直式，未改動
- `story.json` 不再有 width/height（由格式決定尺寸），保留 fps、duration、voice
- `scripts/banner.py` 改取 `output/star_city/horizontal/final.mp4`（t=49.8）當 16:9 螢幕、`output/star_city/vertical/final.mp4`（t=22）當 9:16 手機，須先跑過 story.py。輸出 `docs/banner/banner.png` 與 `social-preview.jpg`（JPEG，因 GitHub 社群預覽上限 1 MB），重新產生後要在 GitHub Settings 手動重新上傳

## 配樂（2026-11-18）
- 選曲順序：1. Alone with the Stars（Tsorthan Grove）、2. Snow Globe (expanded)（Bobjt）、3. Dreams of Utopian Clouds（Tsorthan Grove）。三首皆已在 OpenGameArt 原始頁面用 Chrome 確認為 CC0；最終採用第 1 首，檔案放 `stories/star_city/music/alone_with_the_stars.flac`
- 混音設計（`story.py` 的 `mix` 步驟，輸出 `output/star_city/mix.wav`，`final` 改為合成它）：配樂底床在壓低前約 -27 LUFS；以旁白為 sidechain 壓低配樂，說話時約低 8.7 dB；淡入 1.5 秒、淡出 3 秒；最後兩段式 loudnorm（linear）母帶處理到 -14 LUFS／-1.5 dBTP
- 由 `story.json` 的 `spec.music` 驅動；省略 `spec.music` 就只有旁白，不加配樂
