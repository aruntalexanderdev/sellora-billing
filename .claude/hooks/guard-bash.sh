#!/usr/bin/env bash
# Blocks dangerous Bash commands. Exit 2 = block (stderr is shown to Claude).
input=$(cat)

block() { echo "Blocked by guard-bash: $1" >&2; exit 2; }

echo "$input" | grep -Eq 'git[^"]*push[^"]*(--force([^-]|$)|[[:space:]]-f([[:space:]]|"|$))' && block "force-push is not allowed (use --force-with-lease only with user approval)."
echo "$input" | grep -Eq 'git[^"]*push[^"]*([[:space:]]main([[:space:]]|"|$)|:main([[:space:]]|"|$)|refs/heads/main)' && block "pushing to main is not allowed — open a PR."

# Branch model (CLAUDE.md, Git): analyse_copy and claude_dev change by pull request only.
# The tool input is JSON: a newline inside the command arrives as the two characters \n. Treat it as a command separator, so one
# rule never reads across two commands. "git push" means the push subcommand itself (git, optional global options, push): not
# "git stash push", not prose that merely contains the words.
flat=$(printf '%s' "$input" | sed 's/\\n/;/g')
sq="'"
git_push='(^|[^[:alnum:]_.-])git([[:space:]]+(-[Cc][[:space:]]+[^[:space:]"&;|]+|--[a-z-]+(=[^[:space:]"&;|]+)?))*[[:space:]]+push'
arg_end='([[:space:]]|\\|"|'"$sq"'|\)|&|;|\||$)'
# A refspec whose destination is a protected branch: analyse_copy, +analyse_copy, src:claude_dev, :claude_dev, refs/heads/..., HEAD:refs/heads/...
printf '%s' "$flat" | grep -Eq "$git_push"'([[:space:]]+[^[:space:]"&;|]+)*[[:space:]]+\+?([^[:space:]"&;|:]*:)?(refs/heads/)?(analyse_copy|claude_dev)'"$arg_end" \
  && block "direct pushes to analyse_copy and claude_dev are not allowed — push a work branch (feature/..., fix/..., chore/...) and open a PR."
# A push that names no branch (git push, git push -u origin, git push origin HEAD) pushes the checked-out one: refuse it while a
# protected branch is checked out.
current=$(git -C "${CLAUDE_PROJECT_DIR:-.}" branch --show-current 2>/dev/null)
if [[ "$current" =~ ^(analyse_copy|claude_dev|main)$ ]] \
  && printf '%s' "$flat" | grep -Eq "$git_push"'([[:space:]]+-[^[:space:]"\\&;|]+)*([[:space:]]+origin)?([[:space:]]+HEAD)?([[:space:]]+[0-9]?>[^[:space:]"\\;|]*)*[[:space:]]*(\\|"|'"$sq"'|\)|&|;|\||$)'; then
  block "you are on '$current', which changes by pull request only — create a work branch first (git checkout -b feature/<slug>), then push that."
fi

echo "$input" | grep -Eq 'dotnet ef database drop' && block "dropping a database requires the user to run it manually."
echo "$input" | grep -Eiq '(DROP (TABLE|DATABASE|SCHEMA)|TRUNCATE )' && block "destructive SQL requires explicit user approval and must be run manually."
echo "$input" | grep -Eq 'rm -rf (/|~|\.|\*|backend|frontend|docs)([[:space:]]|"|$)' && block "broad rm -rf is not allowed."

exit 0
