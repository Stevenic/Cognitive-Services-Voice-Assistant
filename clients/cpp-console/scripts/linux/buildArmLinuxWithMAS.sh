#!/bin/bash
clear
cd ../..
if [ ! -d out ]; then
    mkdir out # only create directory if does not exist
fi
if [ ! -d SDK ]; then
    mkdir SDK # only create directory if does not exist
fi

# Determine if the Linux is ARM32 or ARM64
ARCH=$(uname -m)
if [[ $ARCH =~ "v7" ]] # ARM32 OS in use
then
echo "Running on ARM32 Linux"
    #echo "Running on ARM32 Linux, creating lib/lib link to lib/arm32"
LIBLINK="arm32"
LIBFOLDER="Linux-arm"
    #ln -s arm32 lib/lib
else
    #echo "Running on ARM64 Linux, creating lib/lib link to lib/arm64"
    #ln -s arm64 lib/lib
echo "Running on ARM64 Linux"
LIBLINK="arm64"
LIBFOLDER="Linux-arm64"
fi

if [ ! -f ./lib/lib/libMicrosoft.CognitiveServices.Speech.core.so ]; then
echo "Cleaning up libs and include directories that we will overwrite"
rm -R ./lib/*
rm -R ./include/c_api
rm -R ./include/cxx_api

echo "Downloading Speech SDK binaries"
wget -c https://aka.ms/csspeech/linuxbinary -O - | tar -xz -C ./SDK

echo "Copying SDK binaries to lib folder and headers to include"
cp -Rf ./SDK/SpeechSDK*/lib .
cp -Rf ./SDK/SpeechSDK*/include .

echo "Creating lib/lib link to lib/$LIBLINK"
ln -s $LIBLINK lib/lib

echo "Creating SDK/arch to SDK/$LIBFOLDER"
ln -s $LIBFOLDER SDK/arch

else
echo "Speech SDK lib found. Skipping download."
fi

if [ ! -f ./lib/lib/libpma.so ]; then
echo "Downloading Microsoft Audio Stack (MAS) binaries"
curl -L "https://aka.ms/sdsdk-download-linux-$LIBLINK" --output ./SDK/Linux-arm.tgz
tar -xzf ./SDK/Linux-arm.tgz -C ./SDK

echo "Copying MAS binaries to lib folder"
cp -Rf ./SDK/arch/* ./lib/lib
else 
echo "MAS binaries found. Skipping download."
fi

echo "Building Raspberry Pi sample ..."
if g++ -Wno-psabi \
src/common/Main.cpp \
src/linux/LinuxAudioPlayer.cpp \
src/common/AudioPlayerEntry.cpp \
src/common/AgentConfiguration.cpp \
src/common/DeviceStatusIndicators.cpp \
src/common/DialogManager.cpp \
-o ./out/sample.exe \
-std=c++14 \
-D LINUX \
-D MAS \
-L./lib/lib \
-I./include/cxx_api \
-I./include/c_api \
-I./include \
-pthread \
-lstdc++fs \
-lasound \
-lMicrosoft.CognitiveServices.Speech.core;
then
error=0;
else
error=1;
fi

cp ./scripts/run.sh ./out
chmod +x ./out/run.sh

echo Cleaning up downloaded files
rm -R ./SDK

echo Done. To start the demo execute:
echo cd ../../out
echo export LD_LIBRARY_PATH="../lib/${LIBLINK}"
echo ./sample.exe [path_to_configFile]

exit $error