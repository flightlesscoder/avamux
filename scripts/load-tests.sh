#!/usr/bin/env bash
# Runs avamux's load tests (big pages, very long lines, thousands of pictures and files) and writes a report.
# They are skipped in ordinary test runs; this turns them on with AVAMUX_LOAD_TESTS=1.
#
#   scripts/load-tests.sh                 # both suites, sizes as written
#   scripts/load-tests.sh --scale 0.2     # a quick pass with smaller inputs
#   scripts/load-tests.sh --scale 5       # push harder, to find where things break
#   scripts/load-tests.sh --only ui       # just the UI suite (or: core, startup)
#   scripts/load-tests.sh --on-desktop    # startup tests on your own display instead of a virtual one
#
# The startup suite starts the real app as a process (several times per scenario): time to first frame, to a first
# terminal command, to the first wiki page and an edit rendered, plugins, the CLI, and shutting down. On Linux it runs
# inside a virtual KWin session (kwin_wayland --virtual), so no window appears on your screen; AVAMUX_STARTUP_RUNS
# sets how many starts each scenario measures (default 3).
#
# The report (a markdown table: time, memory allocated and held, process size, budget) goes to
# TestResults/load-report-<date>.md and is printed at the end. A row "over budget" marks a measurement slower
# than the app can afford there: a performance gap or capacity limit to look at. Each run starts a new report.
set -euo pipefail
cd "$(dirname "$0")/.."

scale=1
only=""
desktop=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --scale) scale="$2"; shift 2 ;;
    --only) only="$2"; shift 2 ;;
    --on-desktop) desktop=1; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

mkdir -p TestResults
report="$PWD/TestResults/load-report-$(date +%Y%m%d-%H%M%S).md"
export AVAMUX_LOAD_TESTS=1 AVAMUX_LOAD_SCALE="$scale" AVAMUX_LOAD_REPORT="$report"
{
  echo "# avamux load test report"
  echo
  echo "$(date '+%Y-%m-%d %H:%M'), scale $scale, $(uname -sm), $(nproc) cores, .NET $(dotnet --version)"
  echo
} > "$report.head"

status=0
# Everything the tests start comes from this build: the app, and the sample plugins the startup suite loads.
dotnet build avamux.slnx -v quiet -nologo | grep -E "error|Warn|warn" || true

if [[ -z "$only" || "$only" == "core" ]]; then
  dotnet test tests/Avamux.Tests --filter "Category=Load" --logger "console;verbosity=normal" || status=$?
fi
if [[ -z "$only" || "$only" == "ui" ]]; then
  dotnet test tests/Avamux.UiTests --filter "Category=Load" --logger "console;verbosity=normal" || status=$?
fi

if [[ -z "$only" || "$only" == "startup" ]]; then
  startup=(dotnet test tests/Avamux.Tests --no-build --filter "Category=Startup" --logger "console;verbosity=normal")
  if [[ "$desktop" == 0 && "$(uname -s)" == "Linux" ]] && command -v kwin_wayland > /dev/null; then
    # A compositor of its own, drawing to memory, with its own Xwayland: the app's windows open there.
    session="$(mktemp "${TMPDIR:-/tmp}/avamux-startup-session.XXXXXX")"
    result="$session.status"
    {
      echo '#!/usr/bin/env bash'
      echo "cd $(printf '%q' "$PWD")"
      printf '%q ' "${startup[@]}"; echo
      echo "echo \$? > $(printf '%q' "$result")"
    } > "$session"
    chmod +x "$session"
    echo "Startup tests in a virtual display (kwin_wayland --virtual)…"
    kwin_wayland --virtual --xwayland --socket "avamux-startup-$$" --width 1600 --height 1000 --no-lockscreen \
      --exit-with-session "$session" 2> >(grep -v -E "^kwin|xkbcomp|^> |XKEYBOARD|Errors from xkbcomp|^$" >&2) || true
    code="$(cat "$result" 2>/dev/null || echo 1)"
    rm -f "$session" "$result"
    [[ "$code" == 0 ]] || status=$code
  else
    "${startup[@]}" || status=$?
  fi
fi

if [[ -f "$report" ]]; then
  cat "$report.head" "$report" > "$report.tmp" && mv "$report.tmp" "$report"
  # Against the previous report: every measurement now more than 25% (and 20 ms) slower than it was.
  previous="$(ls -1t TestResults/load-report-*.md 2>/dev/null | grep -v -F "$(basename "$report")" | head -1 || true)"
  if [[ -n "$previous" ]]; then
    {
      echo
      echo "## Compared with $(basename "$previous")"
      echo
      awk -F'|' '
        function trim(s) { gsub(/^ +| +$/, "", s); return s }
        FNR == 1 { file++ }
        /^\| / && $4 ~ /[0-9]/ {
          key = trim($2) " / " trim($3); ms = trim($4) + 0
          if (file == 1) old[key] = ms; else if (key in old) {
            seen++
            if (ms > old[key] * 1.25 && ms - old[key] > 20) { printf "- **slower**: %s: %.0f ms, was %.0f ms (+%.0f%%)\n", key, ms, old[key], (ms / old[key] - 1) * 100; slower++ }
            else if (ms < old[key] * 0.8 && old[key] - ms > 20) printf "- faster: %s: %.0f ms, was %.0f ms\n", key, ms, old[key]
          }
        }
        END { if (!seen) print "Nothing in common to compare."; else if (!slower) printf "Nothing is more than 25%% slower (%d measurements compared).\n", seen }
      ' "$previous" "$report"
    } >> "$report"
  fi
fi
rm -f "$report.head"
echo
echo "Report: $report"
[[ -f "$report" ]] && cat "$report"
exit $status
