# Dev/test image: mirrors the benchmark box (Ubuntu 24.04) plus tools for the seed, tests and profiling.
FROM ubuntu:24.04
COPY install.sh /tmp/install.sh
RUN bash /tmp/install.sh && apt-get install -y --no-install-recommends \
      sqlite3 jq openssl nodejs npm git procps linux-tools-generic wrk strace && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://github.com/grafana/k6/releases/download/v2.3.0/k6-v2.3.0-linux-amd64.tar.gz | tar -xz -C /tmp && cp /tmp/k6-*/k6 /usr/local/bin/
RUN useradd -m dev
