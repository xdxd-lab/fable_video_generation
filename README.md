<p align="center">
  <img src="docs/banner/banner.png" alt="Unity Video Generation: Claude × Unity，AI 寫腳本、Unity 算圖，自動產出雙語短影音" width="100%">
</p>

# Unity Video Generation

Claude × Unity：用 Claude 寫腳本與程式、Unity 逐格算圖、ffmpeg 合成旁白與字幕，產出雙語短影音。故事短片同時輸出直式（9:16，例如 Instagram Reels）與橫式（16:9）；30 秒流程介紹目前只有直式。

A pipeline that turns a written script into a bilingual (zh-TW / EN) short video: AI-written script and code, Unity rendering, ffmpeg for narration and subtitles. The story pipeline outputs both vertical (9:16) and horizontal (16:9) video from one command; the 30-second workflow reel is vertical only.

> [!IMPORTANT]
> **請先安裝 [Git LFS](https://git-lfs.com/) 再複製本專案。**
> 場景圖片等二進位檔放在 Git LFS，沒有安裝的話，複製下來只會得到幾百 bytes 的指標檔，不是真正的圖片。
>
> ```sh
> brew install git-lfs      # 或從 https://git-lfs.com/ 下載
> git lfs install
> git clone https://github.com/xdxd-lab/unity_video_generation.git
> ```
>
> 如果已經先複製了，安裝 Git LFS 後在專案內執行 `git lfs pull` 即可補回檔案。

## 成品

| 影片 | 長度 | 版本 | 內容 |
| --- | --- | --- | --- |
| 流程介紹 | 30 秒 | 直式 | 介紹「Claude + Unity → 短影音 → 上傳 Reels」的流程（內容中提到的工具版本為製作當時的版本） |
| 星辰城的小星星 | 60 秒 | 直式＋橫式 | 學徒阿澄與一顆迷路藍色小星星的故事，以六張場景圖為舞台 |

輸出規格：直式 1080×1920、橫式 1920×1080（30 秒流程介紹只有直式）、30 fps、H.264 + AAC、響度 -14 LUFS，中文字幕為主、英文字幕並列。成品影片不放進儲存庫，請依下方步驟自行產生。

## 需要的軟體

- macOS（旁白使用系統內建的 `say` 與台灣中文語音 Meijia）
- Unity 6000.3 LTS（已測試版本），並已在 Unity Hub 登入、取得授權
- ffmpeg（需含 libx264 與 libass）
- Python 3
- Git LFS

## 使用方式

### 30 秒流程介紹

```sh
python3 scripts/build.py animatic                     # 旁白、字幕與分鏡示意片
scripts/unity_render.sh                               # Unity 批次算圖
python3 scripts/build.py final output/unity_render.mp4
```

輸出：`output/final.mp4`

### 60 秒故事短片

一個指令完成全部：合成旁白與時間軸、依格式各自用 Unity 算圖、燒入字幕並合成，預設同時輸出直式與橫式。

```sh
python3 scripts/story.py star_city                    # 等同 all，兩種格式（約 83 秒，Mac Studio M4 Max）
```

分步驟或指定格式（`-f vertical|horizontal|both`，預設 both）：

```sh
python3 scripts/story.py star_city all -f horizontal  # 全流程，只做橫式
python3 scripts/story.py star_city timeline           # 只合成旁白並排時間軸（兩種格式共用）
python3 scripts/story.py star_city render -f vertical # 只做 Unity 算圖
python3 scripts/story.py star_city final -f vertical  # 只做字幕與合成
```

快速檢查畫面：加上 `--stills`，只輸出指定秒數的靜態圖，不算整支影片。

```sh
python3 scripts/story.py star_city render -f vertical --stills 3,20
```

輸出（旁白與時間軸兩種格式共用，其餘依格式放在 `vertical/` 或 `horizontal/`）：

```
output/star_city/narration.wav
output/star_city/timeline.json
output/star_city/<format>/unity_render.mp4   Unity 算圖（無字幕、無聲）
output/star_city/<format>/subtitles.ass
output/star_city/<format>/final.mp4          成品
output/star_city/<format>/still_<t>.png      --stills 的靜態圖
```

`scripts/story_render.sh <name> <vertical|horizontal> [stills]` 是 `story.py` 內部呼叫的 Unity 算圖腳本，會設定 `STORY_FORMAT` 環境變數，一般不需要直接執行。

### 橫幅

```sh
python3 scripts/banner.py
```

輸出 `docs/banner/banner.png`（README 用，2560×1280）與 `docs/banner/social-preview.jpg`（GitHub 社群預覽圖，1280×640）。文字與版面都集中在腳本開頭的常數，修改後重跑即可。需要先產生直式與橫式兩支故事短片（先執行 `python3 scripts/story.py star_city`）：橫向螢幕畫面取自橫式成品，手機畫面取自直式成品。重新產生後，`social-preview.jpg` 需要手動到 GitHub 的 Settings > General > Social preview 重新上傳（為符合 GitHub 1 MB 上限而使用 JPEG）。

## 專案結構

```
script.json                    30 秒片的分鏡與雙語台詞
stories/star_city/story.json   故事短片的分鏡與雙語台詞
stories/star_city/refs/        六張原始場景圖（Git LFS）
stories/star_city/refs_2x/     放大兩倍後交給 Unity 的版本（Git LFS）
scripts/                       旁白、字幕、算圖與合成腳本（story.py 為故事短片的一鍵入口）
unity/Assets/Editor/           ReelBuilder.cs、StoryBuilder.cs：以程式建場景並逐格算圖
docs/plan.md                   製作計畫、技術細節與踩過的坑
```

## 做法重點

- **台詞是唯一來源**：分鏡、台詞與時間都寫在 JSON，旁白、字幕與 Unity 畫面都從它產生。
- **旁白決定時間軸**：每句旁白各自合成、去掉頭尾靜音後排列，Unity 與字幕共用同一份時間軸，畫面不會和聲音錯開。
- **可重現的算圖**：不用 Recorder，也不進 Play 模式。每個動畫都是時間的純函數，逐格 `Camera.Render()` 後把像素直接送進 ffmpeg，同樣的輸入永遠得到同樣的影片。
- **觸控視覺**：觸控效果沿用 mtk-viewer 的風格，以圓點加上向外擴散的圓圈呈現。

更多細節見 [`docs/plan.md`](docs/plan.md)。
