{pkgs ? import <nixpkgs> {}}:
with pkgs;
  mkShell {
    allowUnfree = true;
    name = "h4bot";
    packages = [
      rustup
    ];
    buildInputs = [
      pkg-config
      openssl
      dotnet-sdk_9
    ];
    RUST_BACKTRACE = 1;
  }
