#!/bin/zsh
# Usage: scripts/story_render.sh <name> <vertical|horizontal>              -> output/<name>/<format>/unity_render.mp4
#        scripts/story_render.sh <name> <vertical|horizontal> 1.5,6,12     -> output/<name>/<format>/still_<t>.png
# Needs output/<name>/timeline.json (python3 scripts/story.py <name> timeline). Usually called by scripts/story.py.
set -e
ROOT="${0:A:h:h}"; NAME="$1"; FORMAT="${2:-vertical}"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity}"
export STORY_FORMAT="$FORMAT" REEL_OUT="$ROOT/output/$NAME/$FORMAT" REEL_STILLS="$3" REEL_FFMPEG="$(command -v ffmpeg)"
export STORY_REFS="$ROOT/stories/$NAME/refs_2x" STORY_TIMELINE="$ROOT/output/$NAME/timeline.json"
export STORY_CHARACTER="${STORY_CHARACTER:-v1}" STORY_CHARACTER_SPEC="$ROOT/stories/$NAME/characters/cheng.json"
mkdir -p "$REEL_OUT"
LOG="$REEL_OUT/unity-render.log"
"$UNITY" -batchmode -projectPath "$ROOT/unity" -executeMethod StoryBuilder.Render -logFile "$LOG" || {
  grep -E '\[Story\]|error CS|Exception' "$LOG" | sort -u | head -30; exit 1; }
grep -E '^\[Story\]' "$LOG" | sort -u | tail -4
