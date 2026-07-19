# 交辦:修正 WebGL 部署後 Addressables bundle 404(快取標頭問題)

> 這份文件是給「實際跑 Publishs stack(cloudflared + nginx-proxy + project2 容器)那台主機」上的助手執行的。
> 問題已在開發機上診斷完畢,根因明確,照本文操作即可。

## 背景與根因(不用重新診斷)

**症狀**:玩家開 `https://proxy.pinioncore.dpdns.org/project2/` 時,部分 Addressables bundle 404:

```
RemoteProviderException : Unable to load asset bundle from :
.../StreamingAssets/aa/WebGL/defaultlocalgroup_assets_all_<hash>.bundle
ResponseCode : 404, Method : GET
→ 連鎖 GroupOperation failed → ActorShell 模型載入失敗
```

**根因**:Addressables 的 bundle 檔名帶 content hash,每次重新部署檔名就換;但 origin nginx(`webgl-client` 容器)原本**完全沒送 `Cache-Control`**,Cloudflare 對 `.bin`/`.js` 等副檔名預設會 edge cache 並蓋 `Cache-Control: max-age=14400`(4 小時)。結果:重新部署後,瀏覽器與 CF 邊緣最多 4 小時內仍持有**舊的 `catalog.bin`**,照舊 catalog 去要「上一版檔名」的 bundle → 404。hash 沒變的 bundle(如 monoscripts)則 304 正常,所以症狀是「部分 404、部分正常」。

**修法**:origin 給明確的 `Cache-Control`,Cloudflare 就會遵循 origin、不再自作主張:
- `*.bundle`(檔名含 content hash,內容不可變)→ 快取一年 + `immutable`
- 其餘(`catalog.bin`/`catalog.hash`、`index.html`、`Build/*`、`connection.json`)→ `no-cache`(每次 revalidate;內容沒變 nginx 回 304,86MB 的 wasm 不會重新下載,頻寬成本極低)

## 要做的事

### 1. 更新 `docker/nginx-webgl.conf`

ProjectGame2 repo 的 `docker/nginx-webgl.conf` 已在開發機改好(commit 訊息含 nginx cache header)。若能 `git pull` 就直接拉;不方便拉的話,把該檔整檔換成以下內容:

```nginx
# WebGL client 靜態托管(WebGL 壓縮設為 Disabled,毋需 br/gzip Content-Encoding header)
#
# Cache-Control 策略(修部署後 Addressables 404 的根因):
#   Cloudflare 對 .bin/.js 等副檔名預設會 edge cache 並蓋 max-age=14400(4h),
#   導致重新部署後瀏覽器/邊緣仍拿舊 catalog.bin,去要「上一版 content hash」的
#   bundle 檔名而 404。origin 給明確 Cache-Control 後 Cloudflare 會遵循 origin。
#   - *.bundle 檔名含 content hash,可永久快取(immutable)
#   - 其餘(catalog.bin/hash、index.html、Build/*、connection.json)一律 no-cache:
#     每次 revalidate,內容沒變時 nginx 回 304,頻寬成本極低
map $uri $webgl_cache_control {
    ~\.bundle$  "public, max-age=31536000, immutable";
    default     "no-cache";
}

server {
    listen 80;
    root /usr/share/nginx/html;
    index index.html;

    add_header Cache-Control $webgl_cache_control always;

    location ~ \.wasm$ { default_type application/wasm; }
    location ~ \.data$ { default_type application/octet-stream; }
}
```

注意:`map` 必須在 `server {}` 之外(檔案頂部)。這檔案是以 bind mount 掛進容器的
(`./nginx-webgl.conf:/etc/nginx/conf.d/default.conf:ro`),改完檔案要重啟容器才生效。

### 2. 重啟 webgl-client 容器

```bash
docker restart pinioncore-project2-client
# 若也有跑診斷用的 webgl-client-dev(8082),一併重啟
```

nginx-proxy(`/project2/` 反代那層)**不用改**:`proxy_pass` 預設會透傳 origin 的
`Cache-Control`。它對 `connection.json` 的 `location =` 覆寫也不受影響(`.json` 不在
CF 預設快取副檔名清單)。

### 3. 清 Cloudflare 快取(一次性)

CF 邊緣現在還存著改標頭前的舊回應(實測 `catalog.bin` 與 `Build/webgl-client.loader.js`
都是 `cf-cache-status: HIT`)。到 Cloudflare dashboard 對 zone 執行 **Purge Everything**
(或至少 purge `proxy.pinioncore.dpdns.org/project2/*` 前綴)。
沒有 dashboard 權限就請主人處理;不 purge 的話舊快取最多再活 4 小時後自然過期。

### 4. 驗證

容器重啟 + purge 後,從任何機器執行:

```bash
# a) catalog.bin 必須是 no-cache,且 cf-cache-status 不再是 HIT(BYPASS/DYNAMIC 皆可)
curl -sI "https://proxy.pinioncore.dpdns.org/project2/StreamingAssets/aa/catalog.bin" \
  | grep -iE "cache-control|cf-cache-status"
# 預期:Cache-Control: no-cache

# b) bundle 必須是 immutable(拿 catalog.bin 裡實際引用的檔名測;下面指令自動抽一個)
curl -s "https://proxy.pinioncore.dpdns.org/project2/StreamingAssets/aa/catalog.bin" -o /tmp/cat.bin
BUNDLE=$(grep -ao '[ -~]\{30,\}' /tmp/cat.bin | grep -o '[a-z0-9_]*_assets_all_[a-f0-9]*\.bundle' | head -1)
curl -sI "https://proxy.pinioncore.dpdns.org/project2/StreamingAssets/aa/WebGL/$BUNDLE" \
  | grep -iE "^HTTP|cache-control"
# 預期:HTTP 200 + Cache-Control: public, max-age=31536000, immutable

# c) catalog 引用的所有 bundle 都存在(自洽性檢查,任何一顆 404 都不行)
for B in $(grep -ao '[ -~]\{30,\}' /tmp/cat.bin | grep -o '[a-zA-Z0-9_]*\.bundle' | sort -u); do
  printf "%s " $(curl -s -o /dev/null -w "%{http_code}" \
    "https://proxy.pinioncore.dpdns.org/project2/StreamingAssets/aa/WebGL/$B"); echo $B
done
# 預期:全部 200
```

最後開瀏覽器硬刷新(Ctrl+Shift+R)實際進遊戲,console 不應再有
`RemoteProviderException`/`ActorShell 模型載入失敗`。

## 完成標準

1. `catalog.bin` 回應 `Cache-Control: no-cache`;`*.bundle` 回應 `immutable`。
2. catalog 引用的 bundle 全部 200。
3. 瀏覽器實際載入遊戲無 404。
4. (此後)每次重新部署 WebGL 後,玩家重新整理頁面即拿到新 catalog,不再有 4 小時的 404 窗口。

## 執行結果(2026-07-19,stack 主機)

- 步驟 1–2 完成:conf 已是新版(bind mount 同一檔),重啟 `pinioncore-project2-client` 後 origin 與 `pinioncore-proxy` 層均正確回 `no-cache` / `immutable`。
- 步驟 3 未執行:本機僅有 certbot 用的 DNS-only token(`ssl-certs/cloudflare.ini`),無 cache purge 權限。實測邊緣現存的 `catalog.bin` ETag 與 origin 一致(快取的就是最新版),不 purge 也無舊內容在線上;該筆快取當日 14:45 UTC 前自然過期。
- 步驟 4 完成:catalog 實際引用的 4 顆 bundle 全部 200;瀏覽器載入遊戲無 `RemoteProviderException`。(4c 指令的 grep 會把完整檔名的 hash 後綴誤抽成獨立檔名而報 404,對照完整檔名皆 200 即可忽略。)
- **文件假設修正**:origin 給明確 Cache-Control 後,CF **edge 端**確實遵循(`catalog.bin` 每次 `REVALIDATED`),但 zone 的 **Browser Cache TTL 設定為 4 小時**,會把 origin 的 `no-cache` 蓋成瀏覽器端 `max-age=14400`(僅在設定值大於 origin max-age 時覆蓋,故 bundle 的一年 `immutable` 不受影響)。已於同日在 dashboard 將 Browser Cache TTL 改為 **Respect Existing Headers**,實測 `catalog.bin` 過 CF 後正確回 `no-cache`,全部完成標準達成。
