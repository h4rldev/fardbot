default: 
  just --list

@run:
  cargo run --release

@release:
  cargo build --release

@debug:
  cargo build
