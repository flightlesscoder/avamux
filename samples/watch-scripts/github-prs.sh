#!/bin/sh
# Example script watch built on a tool you already have signed in: the GitHub CLI.
# (avamux also has a built-in "github-pr" kind; this shows how to wrap any CLI instead.)
#
#   avamux watch add "My PRs" --kind command --set "command=/path/to/github-prs.sh" --set repo=owner/name
#
# Requires: gh, jq.
repo="${AVAMUX_WATCH_REPO:?set repo=owner/name}"

gh pr list --repo "$repo" --author "@me" --json number,title,url,isDraft,reviewDecision | jq -c --arg repo "$repo" '{
  status: (if length == 0 then "ok" elif any(.[]; .reviewDecision == "CHANGES_REQUESTED") then "warn" else "info" end),
  summary: "\(length) open pull request(s) by me in \($repo)",
  link: "https://github.com/\($repo)/pulls/@me",
  items: map({
    id: (.number | tostring),
    title: "#\(.number) \(.title)",
    status: (if .isDraft then "draft" else (.reviewDecision // "open" | ascii_downcase) end),
    link: .url
  })
}'
