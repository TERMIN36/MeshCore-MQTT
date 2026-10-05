#!/bin/sh
# Пишет строку режима доступа в статику панели. Вызывается entrypoint nginx до старта.
mode=${ACCESS_MODE-Личное пользование}
escaped=$(printf '%s' "$mode" | awk 'BEGIN { ORS="" }
{
  gsub(/\\/, "\\\\")
  gsub(/"/, "\\\"")
  gsub(/\r/, "")
  if (NR > 1) printf "\\n"
  printf "%s", $0
}')
printf 'window.ACCESS_MODE="%s";\n' "$escaped" > /usr/share/nginx/html/config.js
