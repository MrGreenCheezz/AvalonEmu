# Internal API

The internal API is an optional JSON bridge for a website process running on the same machine as the server. It is not
part of the public web panel. It is disabled by default under both presets.

## Enable it

Add these keys to `config/common.jsonc`:

```jsonc
"Web": {
  "Enabled": false,
  "BindAddress": "127.0.0.1",
  "Port": 8080,
  "Registration": true,
  "SessionMinutes": 30,
  "ApiEnabled": true,
  "ApiBindAddress": "127.0.0.1",
  "ApiPort": 5059,
  "ApiToken": "replace-with-at-least-32-random-characters"
}
```

`Web:ApiEnabled` is the feature switch. `Web:ApiToken` is a secret: do not commit the real value, put it in a browser,
or return it from an HTTP endpoint. Generate a long random value and store it only in the server and website
configuration.

The API refuses to start if:

- `Web:ApiBindAddress` is not a loopback IPv4 address.
- `Web:ApiToken` is shorter than 32 characters.
- `Web:ApiPort` conflicts with another configured listener.

Use plain HTTP only over loopback. If the website runs in a container or on another host, expose it through a private
reverse proxy or private network and terminate TLS there; do not bind this API directly to the public internet.

## Authentication

Every request must include the token in one of these forms:

```http
X-Internal-Token: <token>
```

```http
Authorization: Bearer <token>
```

The token comparison is constant-time. A missing or invalid token returns `401`.

Paths are relative to:

```text
http://127.0.0.1:5059/internal/v1/
```

## Response format

Successful responses:

```json
{
  "ok": true,
  "data": {}
}
```

Errors:

```json
{
  "ok": false,
  "error": {
    "code": "INVALID_CREDENTIALS",
    "message": "Invalid account name or password."
  }
}
```

The API never returns password hashes. Unexpected exception details are logged on the server and replaced by a generic
`INTERNAL_ERROR` response.

## Routes

| Method | Path | Purpose |
|---|---|---|
| `GET` | `status` | Server status, builds, uptime and registration state |
| `GET` | `online` | Paginated online players; filters: `q`, `guildId`, `factionId`, `page`, `limit` |
| `POST` | `auth/login` | Validate an account password and return its account and character summary |
| `POST` | `accounts` | Create an account with the normal account password rules |
| `GET` | `accounts/{accountId}` | Account summary without characters |
| `GET` | `accounts/{accountId}/characters` | Account summary with character summaries |
| `POST` | `accounts/{accountId}/password/change` | Change a password with the current password |
| `POST` | `accounts/{accountId}/password/reset` | Privileged password reset |
| `POST` | `accounts/{accountId}/sessions/revoke` | End the account's online game sessions |
| `GET` | `guilds` | Paginated guild directory; filters: `q`, `page`, `limit` |
| `GET` | `guilds/{guildId}` | Guild details |
| `GET` | `guilds/{guildId}/members` | Guild members and online state |
| `GET` | `factions` | Summary for the six factions |
| `GET` | `factions/{factionId}/ranking` | Faction ranking; filters: `metric=standing\|points`, `limit` |

### Status

```http
GET /internal/v1/status
X-Internal-Token: <token>
```

Returns player and bot counts, loaded maps, uptime, server/client builds, preset and registration state.

### Online players

```http
GET /internal/v1/online?page=1&limit=50&q=name
X-Internal-Token: <token>
```

`limit` is clamped to `1..100`. Guild and faction filters may be combined with the text query.

### Login

```http
POST /internal/v1/auth/login
Content-Type: application/json
X-Internal-Token: <token>

{
  "name": "account-name",
  "password": "account-password"
}
```

The login endpoint uses the same account lock and banned-account state as the game. A wrong password returns `401`; a
locked account returns `423`; a banned account returns `403`.

### Register

```http
POST /internal/v1/accounts
Content-Type: application/json
X-Internal-Token: <token>

{
  "name": "account-name",
  "password": "account-password"
}
```

The API is privileged. A website that offers public registration should check `registrationOpen` from `status` and hide
or close its registration page when it is false.

### Change password

```http
POST /internal/v1/accounts/{accountId}/password/change
Content-Type: application/json
X-Internal-Token: <token>

{
  "currentPassword": "old-password",
  "newPassword": "new-password"
}
```

The current password must match. Existing online game sessions are closed after a successful change.

### Reset password

```http
POST /internal/v1/accounts/{accountId}/password/reset
Content-Type: application/json
X-Internal-Token: <token>

{
  "newPassword": "new-password"
}
```

This is an administrative operation for the trusted website backend. Do not expose it directly to a browser. Existing
online game sessions are closed after a successful reset.

### Revoke sessions

```http
POST /internal/v1/accounts/{accountId}/sessions/revoke
Content-Type: application/json
X-Internal-Token: <token>

{}
```

### Faction ranking

```http
GET /internal/v1/factions/1/ranking?metric=points&limit=20
X-Internal-Token: <token>
```

Faction IDs are `1..6`. Rank entries include both raw fixed-point values and decimal values. The server's internal
`a7m` convention is `10000 = 1.0`.

## PHP integration example

Keep the internal token on the PHP server:

```php
$token = getenv('AVALON_INTERNAL_API_TOKEN');
$context = stream_context_create([
    'http' => [
        'method' => 'GET',
        'header' => "X-Internal-Token: {$token}\r\n",
        'timeout' => 5,
    ],
]);

$json = file_get_contents('http://127.0.0.1:5059/internal/v1/status', false, $context);
$response = json_decode($json, true, flags: JSON_THROW_ON_ERROR);

if (($response['ok'] ?? false) !== true) {
    throw new RuntimeException('Internal API request failed');
}
```

The browser must call the PHP application, not this API. Never embed the internal token in HTML, JavaScript, URLs or
public PHP responses.
