{pkgs ? import <nixpkgs> {}}:
with pkgs;
mkShell {
  allowUnfree = true;
  name = "h4bot";
  packages = [
    rustup
  ];
  buildInputs = [
     pkg-config openssl
  ];
  # Additional configuration (if needed)
  RUST_BACKTRACE = 1;
}
