#!/usr/bin/env bash
set -euo pipefail

APP_ID="${APP_ID:?set APP_ID env var}"
INSTALL_ID="${APP_INSTALLATION_ID:?set APP_INSTALLATION_ID env var}"
KEY_FILE="${HYDRA_KEY_FILE:-hydra-private-key.pem}"

pip install pyjwt -q 2>/dev/null

TOKEN=$(python3 -c "
import time, jwt, urllib.request, json
from pathlib import Path
key = Path('$KEY_FILE').read_text()
now = int(time.time())
jwt_token = jwt.encode({'iat': now - 60, 'exp': now + 600, 'iss': '$APP_ID'}, key, algorithm='RS256')
req = urllib.request.Request(
    'https://api.github.com/app/installations/$INSTALL_ID/access_tokens',
    data=b'',
    headers={'Authorization': f'Bearer {jwt_token}', 'Accept': 'application/vnd.github+json'},
    method='POST')
token = json.loads(urllib.request.urlopen(req).read())['token']
print(token, end='')
")

echo "$TOKEN"
