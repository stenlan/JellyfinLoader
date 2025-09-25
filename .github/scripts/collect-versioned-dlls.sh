#!/usr/bin/env bash
pattern="./JellyfinLoaderStub/build/*/bin/Release/*/JellyfinLoaderStub.dll"
regex_pattern="${pattern//\*/(.*)}"
mkdir -p ./collected

for match in $pattern; do
  [[ "$match" =~ $regex_pattern ]]
  cp "$match" "./collected/JellyfinLoaderStub_${BASH_REMATCH[1]}.dll"
done