# Monitoring (Prometheus + Grafana)

Prometheus scrapes `GET /metrics` on the four production services every 15s; Grafana shows the
**Custodian → Custodian services** dashboard (request rate, 5xx ratio, p95 latency, top endpoints, 4xx,
CPU, memory, GC). Application Insights stays the place for traces, logs and the application map.

Runs on `vm-custodian-monitoring` (rg-custodian-monitoring, East Asia, Ubuntu 24.04) with Docker Compose.
The VM's network security group only allows ports 22, 3000 and 9090 from the operator's IP.

## How `/metrics` is protected

The services expose `/metrics` only when `Metrics__ScrapeKey` is set on the App Service app (it returns
404 otherwise outside Development). Prometheus sends HTTP Basic auth with user `prometheus` and that key
as the password, read from `prometheus/scrape_key`. The same key is set on all four apps.

## Setup on a fresh VM

```bash
sudo apt-get update && sudo apt-get install -y docker.io docker-compose-v2
# copy this folder to ~/monitoring, then inside it:
read -rs -p "Scrape key: " K; echo; printf '%s' "$K" > prometheus/scrape_key; unset K
read -rs -p "Grafana admin password: " P; echo; printf 'GRAFANA_ADMIN_PASSWORD=%s\n' "$P" > .env; unset P
chmod 600 .env && sudo chown 65534:65534 prometheus/scrape_key && sudo chmod 400 prometheus/scrape_key
sudo docker compose up -d
```

`prometheus/scrape_key` must contain exactly the key (64 hex characters): a target that shows
`401 Unauthorized` on Prometheus's **Status → Target health** page means the key does not match.

- Grafana: `http://<vm-ip>:3000` (user `admin`)
- Prometheus: `http://<vm-ip>:9090`

If the App Service hostnames change, update the targets in `prometheus/prometheus.yml` and run
`sudo docker compose restart prometheus`.
