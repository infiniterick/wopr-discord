FROM hai-arm:latest
WORKDIR /
COPY ./bin/Release/netcoreapp3.1/linux-arm64/publish/* ./
ENTRYPOINT ["/wopr-discord", "/secrets"]