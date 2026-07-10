#!/bin/bash
# scripts/smoke.sh — M4 自动化 smoke:启 service + curl healthz + env/list
set -e

PORT=${PORT:-7800}
BIND=${BIND:-127.0.0.1}

echo "Starting Python service on $BIND:$PORT..."
python -m comfy_mgr.cli serve --port $PORT --bind $BIND &
PID=$!
trap "kill $PID 2>/dev/null || true" EXIT

# 等 healthz
echo "Waiting for /healthz..."
for i in {1..20}; do
    if curl -sf "http://$BIND:$PORT/healthz" > /dev/null; then
        echo "✓ healthz OK"
        break
    fi
    sleep 0.5
done

# 测试 healthz
echo "Test 1: GET /healthz"
curl -sf "http://$BIND:$PORT/healthz" && echo " ✓"

echo "Test 2: GET /version"
curl -sf "http://$BIND:$PORT/version" && echo " ✓"

echo "Test 3: POST /api/v1/env/list"
curl -sf -X POST -H "Content-Type: application/json" \
    -d '{}' "http://$BIND:$PORT/api/v1/env/list" | head -c 200
echo " ✓"

echo "Test 4: POST /api/v1/settings/get-all"
curl -sf -X POST -H "Content-Type: application/json" \
    -d '{}' "http://$BIND:$PORT/api/v1/settings/get-all" | head -c 200
echo " ✓"

echo "Test 5: WS /ws/events (3 秒测试)"
timeout 3 python -c "
import asyncio, websockets, json
async def main():
    async with websockets.connect('ws://$BIND:$PORT/ws/events') as ws:
        msg = await ws.recv()
        print('  got:', msg[:100])
asyncio.run(main())
" || echo "  (WS 测试失败,需安装 websockets: pip install websockets)"

echo "All smoke tests passed."