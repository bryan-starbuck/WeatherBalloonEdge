coverlet .\bin\Debug\netcoreapp2.1\BalloonModule.Test.dll --target "dotnet" --targetargs "test BalloonModule.Test.csproj --no-build" --format lcov --output lcov.info --include "[*Balloon*]*"