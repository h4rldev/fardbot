{pkgs ? import <nixpkgs> {}}:
with pkgs;
  mkShell {
    allowUnfree = true;
    name = "h4bot";
    buildInputs = [
      pkg-config
      openssl
      dotnet-sdk_9
      just
    ];
    RUST_BACKTRACE = 1;
  }
