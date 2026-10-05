# Upload to Zipline

In Settings → Sharing, configure:

- Destination URL: `https://<your-zipline>/api/upload`
- Header: `Authorization` with your Zipline API token as its value
- Multipart file field: `file`
- Response JSON path: `files.0.url`
- Timeout: choose a value up to 300 seconds

Header values are stored encrypted with Windows DPAPI for the current user. Zipline's [Shell Script uploader guide](https://zipline.diced.sh/docs/guides/uploaders/shell-script) documents `POST /api/upload`, the `file` multipart field, and the returned URL at `files[0].url`. Its [API authentication guide](https://zipline.diced.sh/docs/api/authentication) specifies `Authorization: <your token>`.
