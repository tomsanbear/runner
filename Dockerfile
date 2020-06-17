FROM debian:buster as builder

# Update Base
RUN apt-get update && apt-get upgrade -y && apt-get install -y wget curl build-essential git

# Install Pre-requisites
RUN wget https://packages.microsoft.com/config/debian/10/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
RUN dpkg -i packages-microsoft-prod.deb
RUN apt-get update && apt-get install -y dotnet-sdk-3.1

# Add dev repo
ADD . /etc/runner

# Build Dir
WORKDIR /etc/runner/src

# Build the Binaries
RUN ./dev.sh build

# Start command
CMD [ "/bin/bash", "/etc/runner/_layout/run.sh" ]