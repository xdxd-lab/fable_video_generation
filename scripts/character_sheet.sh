#!/bin/zsh
# Usage: scripts/character_sheet.sh <story> [character-id]   -> output/<story>/characters/<id>_sheet.png
# Renders v1 and v2 of the apprentice side by side in the poses the story uses, for design review.
set -e
ROOT="${0:A:h:h}"; NAME="$1"; ID="${2:-cheng}"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity}"
export SHEET_OUT="$ROOT/output/$NAME/characters/${ID}_sheet.png"
export STORY_CHARACTER_SPEC="$ROOT/stories/$NAME/characters/$ID.json"
mkdir -p "${SHEET_OUT:h}"
LOG="${SHEET_OUT:h}/unity-sheet.log"
"$UNITY" -batchmode -projectPath "$ROOT/unity" -executeMethod StoryBuilder.RenderSheet -logFile "$LOG" || {
  grep -E '\[Story\]|error CS|Exception' "$LOG" | sort -u | head -30; exit 1; }
grep -E '^\[Story\]' "$LOG" | sort -u | tail -3
