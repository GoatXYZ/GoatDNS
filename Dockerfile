# Reproducible build + test of the managed projects that DON'T need Windows.
#
# What this builds: GoatDNS.Core, GoatDNS.Service, GoatDNS.WinDivert, and runs GoatDNS.Tests.
# These target net10.0 / net10.0-windows and cross-compile on Linux via EnableWindowsTargeting
# (set in Directory.Build.props for non-Windows hosts).
#
# What this CANNOT build: GoatDNS.App (WinUI 3). Its XAML and .pri resource compilers are
# Windows-only native tools — no Linux container can run them. Build the app on a Windows machine
# (Visual Studio 2022 or `dotnet build` with the Windows workloads).
#
#   docker build -t goatdns-build .          # build + test in one shot (a red test fails the image)
#   docker build --target export -o out .    # also copy the published Service binaries to ./out

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .

# Build the Windows-targeted managed projects and run the full test suite. Restore is implicit;
# a failing test fails the image, so a green image is a real guarantee. xUnit v3 builds a runnable
# executable, so we exec it directly (the .NET 10 SDK dropped the old VSTest `dotnet test` path).
RUN dotnet build GoatDNS.Service/GoatDNS.Service.csproj -c Release \
 && dotnet build GoatDNS.WinDivert/GoatDNS.WinDivert.csproj -c Release \
 && dotnet build GoatDNS.Tests/GoatDNS.Tests.csproj     -c Release \
 && dotnet exec GoatDNS.Tests/bin/Release/net10.0/GoatDNS.Tests.dll

# Publish the service (framework-dependent) so artifacts can be pulled from the image.
RUN dotnet publish GoatDNS.Service/GoatDNS.Service.csproj -c Release -o /out/service

FROM scratch AS export
COPY --from=build /out /
