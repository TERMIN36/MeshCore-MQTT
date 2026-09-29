#!/bin/sh
set -eu
TOKEN=${INTERNAL_TOKEN:-dev-internal-token}
API_IP=""
i=0
while [ "$i" -lt 30 ]; do
  API_IP=$(getent hosts api | awk '{ print $1; exit }')
  if [ -n "$API_IP" ]; then
    break
  fi
  i=$((i + 1))
  sleep 1
done
if [ -z "$API_IP" ]; then
  echo "api host not found" >&2
  exit 1
fi
sed -e "s|__TOKEN__|${TOKEN}|g" -e "s|__API_HOST__|${API_IP}|g" /etc/mosquitto/mosquitto.conf.template > /tmp/mosquitto.conf
exec /usr/sbin/mosquitto -c /tmp/mosquitto.conf
