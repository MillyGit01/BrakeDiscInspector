# Ejemplos curl — Octubre 2025

## 1. Variables
```bash
BASE=http://127.0.0.1:8000
ROLE=master
ROI=inspection-1
API_KEY=demo-token
MM=0.021
```

Añade `-H "X-API-Key: $API_KEY"` si el backend lo exige.

## 2. Health
```bash
curl -s $BASE/health | jq
```

## 3. Fit OK
```bash
curl -X POST "$BASE/fit_ok" \
  -F "role_id=$ROLE" \
  -F "roi_id=$ROI" \
  -F "mm_per_px=$MM" \
  -F "images=@samples/$ROLE/$ROI/ok_001.png"
```

## 4. Calibrate NG
```bash
curl -X POST "$BASE/calibrate_ng" \
  -H 'Content-Type: application/json' \
  -d '{
        "role_id": "'$ROLE'",
        "roi_id": "'$ROI'",
        "mm_per_px": '$MM',
        "ok_scores": [0.2, 0.25, 0.3],
        "ng_scores": [0.7, 0.8],
        "score_percentile": 0.995,
        "area_mm2_thr": 12.5
      }'
```

## 5. Infer
```bash
curl -X POST "$BASE/infer" \
  -F "role_id=$ROLE" \
  -F "roi_id=$ROI" \
  -F "mm_per_px=$MM" \
  -F "image=@samples/$ROLE/$ROI/infer.png" \
  -F 'shape={"kind":"annulus","cx":224,"cy":224,"r":210,"r_inner":140}'
```

La respuesta incluye `score`, `threshold`, `heatmap_png_base64`, `regions`, `token_shape`.

## 6. Manifest
```bash
curl -s $BASE/manifests/$ROLE/$ROI | jq
```
