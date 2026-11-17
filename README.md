<p align="center">
  <img src="docs/banner/banner.png" alt="Unity Video Generation: Claude × Unity，AI 寫腳本、Unity 算圖，自動產出雙語短影音" width="100%">
</p>

# Unity Video Generation

Claude × Unity：用 Claude 寫腳本與程式、Unity 逐格算圖、ffmpeg 合成旁白與字幕，產出雙語短影音。目標同時涵蓋直式（9:16，例如 Instagram Reels）與橫式（16:9）；目前已完成的影片都是直式 1080×1920，橫式輸出尚在規劃中、還沒實作。

A pipeline that turns a written script into a bilingual (zh-TW / EN) short video: AI-written script and code, Unity rendering, ffmpeg for narration and subtitles. Built for both vertical (9:16) and horizontal (16:9) video; the videos produced so far are vertical, and horizontal output is planned but not implemented yet.

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

| 影片 | 長度 | 內容 |
| --- | --- | --- |
| 流程介紹 | 30 秒 | 介紹「Claude + Unity → 短影音 → 上傳 Reels」的流程（內容中提到的工具版本為製作當時的版本） |
| 星辰城的小星星 | 60 秒 | 學徒阿澄與一顆迷路藍色小星星的故事，以六張場景圖為舞台 |

目前輸出規格（直式）：1080×1920、30 fps、H.264 + AAC、響度 -14 LUFS，中文字幕為主、英文字幕並列。成品影片不放進儲存庫，請依下方步驟自行產生。

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

```sh
python3 scripts/story.py star_city timeline           # 合成旁白，依旁白長度排出時間軸
scripts/story_render.sh star_city                     # Unity 批次算圖
python3 scripts/story.py star_city final output/star_city/unity_render.mp4
```

輸出：`output/star_city/final.mp4`

兩支算圖腳本都可以加上時間參數，例如 `scripts/story_render.sh star_city 3,20,45`，只輸出指定秒數的靜態圖，方便快速檢查畫面。

### 橫幅

```sh
python3 scripts/banner.py
```

輸出 `docs/banner/banner.png`（README 用，2560×1280）與 `docs/banner/social-preview.jpg`（GitHub 社群預覽圖，1280×640）。文字與版面都集中在腳本開頭的常數，修改後重跑即可。需要先產生故事短片，因為手機畫面取自其中一格。

## 專案結構

```
script.json                    30 秒片的分鏡與雙語台詞
stories/star_city/story.json   故事短片的分鏡與雙語台詞
stories/star_city/refs/        六張原始場景圖（Git LFS）
stories/star_city/refs_2x/     放大兩倍後交給 Unity 的版本（Git LFS）
scripts/                       旁白、字幕、算圖與合成腳本
unity/Assets/Editor/           ReelBuilder.cs、StoryBuilder.cs：以程式建場景並逐格算圖
docs/plan.md                   製作計畫、技術細節與踩過的坑
```

## 做法重點

- **台詞是唯一來源**：分鏡、台詞與時間都寫在 JSON，旁白、字幕與 Unity 畫面都從它產生。
- **旁白決定時間軸**：每句旁白各自合成、去掉頭尾靜音後排列，Unity 與字幕共用同一份時間軸，畫面不會和聲音錯開。
- **可重現的算圖**：不用 Recorder，也不進 Play 模式。每個動畫都是時間的純函數，逐格 `Camera.Render()` 後把像素直接送進 ffmpeg，同樣的輸入永遠得到同樣的影片。
- **觸控視覺**：觸控效果沿用 mtk-viewer 的風格，以圓點加上向外擴散的圓圈呈現。

更多細節見 [`docs/plan.md`](docs/plan.md)。
