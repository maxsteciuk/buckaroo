cmake_minimum_required(VERSION 3.15)
project(buckaroo)

include(ExternalProject)

if(CMAKE_SYSTEM_NAME MATCHES FreeBSD)
    set(triple x86_64-unknown-freebsd)
elseif(CMAKE_SYSTEM_NAME MATCHES Linux)
    set(triple x86_64-unknown-linux-gnu)
else()
    set(triple x86_64-unknown-unknown)
endif()

ExternalProject_Add(warp
    GIT_REPOSITORY    git@github.com:maxsteciuk/warp.git
    BUILD_IN_SOURCE true
    PREFIX ${CMAKE_BINARY_DIR}
    INSTALL_COMMAND ${CMAKE_COMMAND} -E copy_directory ${CMAKE_BINARY_DIR}/src/warp/target/${triple} ${CMAKE_SOURCE_DIR}/warp-release
)

# Dependencies:
# 1. mono
# 2. msbuild
# 3. fsharpc
set(BUILD_BUCKAROO msbuild /r /p:Configuration=Release)

add_custom_target(buckaroo ALL
                  WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
                  COMMENT "Compiling buckaroo"
                  COMMAND ${BUILD_BUCKAROO}
)
