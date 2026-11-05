#!/usr/bin/env bash
# Blocks dangerous Bash commands. Exit 2 = block (stderr is shown to Claude).
input=$(cat)

block() { echo "Blocked by guard-bash: $1" >&2; exit 2; }

echo "$input" | grep -Eq 'git[^"]*push[^"]*(--force([^-]|$)|[[:space:]]-f([[:space:]]|"|$))' && block "force-push is not allowed (use --force-with-lease only with user approval)."
echo "$input" | grep -Eq 'git[^"]*push[^"]*([[:space:]]main([[:space:]]|"|$)|:main([[:space:]]|"|$)|refs/heads/main)' && block "pushing to main is not allowed — open a PR."
echo "$input" | grep -Eq 'dotnet ef database drop' && block "dropping a database requires the user to run it manually."
echo "$input" | grep -Eiq '(DROP (TABLE|DATABASE|SCHEMA)|TRUNCATE )' && block "destructive SQL requires explicit user approval and must be run manually."
echo "$input" | grep -Eq 'rm -rf (/|~|\.|\*|backend|frontend|docs)([[:space:]]|"|$)' && block "broad rm -rf is not allowed."

exit 0
