# Dev/test image: mirrors the benchmark box (Ubuntu 24.04) plus tools for the seed, tests and profiling.
FROM ubuntu:24.04
COPY install.sh /tmp/install.sh
RUN bash /tmp/install.sh && apt-get install -y --no-install-recommends \
      sqlite3 jq openssl nodejs npm git procps linux-tools-generic wrk && rm -rf /var/lib/apt/lists/*
RUN useradd -m dev
