#!/bin/zsh
# Usage: scripts/story_render.sh <name>            -> output/<name>/unity_render.mp4
#        scripts/story_render.sh <name> 1.5,6,12   -> output/<name>/still_<t>.png
set -e
ROOT="${0:A:h:h}"; NAME="$1"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity}"
export REEL_OUT="$ROOT/output/$NAME" REEL_STILLS="$2" REEL_FFMPEG="$(command -v ffmpeg)"
export STORY_REFS="$ROOT/stories/$NAME/refs_2x" STORY_TIMELINE="$ROOT/output/$NAME/timeline.json"
LOG="$REEL_OUT/unity-render.log"
"$UNITY" -batchmode -projectPath "$ROOT/unity" -executeMethod StoryBuilder.Render -logFile "$LOG" || {
  grep -E '\[Story\]|error CS|Exception' "$LOG" | sort -u | head -30; exit 1; }
grep -E '^\[Story\]' "$LOG" | sort -u | tail -4
