#!/usr/local/bin/bash
  
export CppCompilerAndLinker=clang

chmod +x ./warp-packer
./warp-release/release/warp-packer --version

dotnet publish ./buckaroo-cli/ -c Release -r freebsd-x64

mkdir -p warp
rm -rf ./warp/buckaroo-freebsd

./warp-release/release/warp-packer --arch freebsd-x64 --exec buckaroo-cli --input_dir ./buckaroo-cli/bin/Release/netcoreapp2.1/ --output warp/buckaroo-freebsd
./warp/buckaroo-freebsd
