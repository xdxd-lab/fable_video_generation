#!/bin/zsh
# Usage: scripts/unity_render.sh            -> output/unity_render.mp4
#        scripts/unity_render.sh 1.5,6,12   -> output/still_<t>.png
set -e
ROOT="${0:A:h:h}"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity}"
export REEL_OUT="$ROOT/output" REEL_STILLS="$1" REEL_FFMPEG="$(command -v ffmpeg)"
"$UNITY" -batchmode -projectPath "$ROOT/unity" -executeMethod ReelBuilder.Render -logFile "$ROOT/output/unity-render.log" || {
  grep -E '\[Reel\]|error CS|Exception' "$ROOT/output/unity-render.log" | head -30; exit 1; }
grep -E '\[Reel\]' "$ROOT/output/unity-render.log" | grep -v '^\s' | sort -u | tail -5
