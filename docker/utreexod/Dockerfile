# syntax=docker/dockerfile:1

FROM golang:1.25-bookworm AS build

ARG UTREEXOD_REPO=https://github.com/utreexo/utreexod.git
ARG UTREEXOD_REF=master

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

RUN set -eux; \
    if git clone --depth 1 --branch "$UTREEXOD_REF" "$UTREEXOD_REPO" utreexod; then \
        :; \
    else \
        git clone "$UTREEXOD_REPO" utreexod; \
        cd utreexod; \
        git checkout "$UTREEXOD_REF"; \
        cd ..; \
    fi; \
    cd utreexod; \
    go build -trimpath -ldflags="-s -w" -o /out/utreexod .

FROM debian:bookworm-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out/utreexod /usr/local/bin/utreexod

RUN mkdir -p /data

WORKDIR /data

EXPOSE 38332 38333

CMD ["utreexod"]
