include(${CMAKE_CURRENT_LIST_DIR}/../libraries.cmake)

set(VCPKG_TARGET_ARCHITECTURE arm64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_BUILD_TYPE release)

cmake_host_system_information(RESULT _TOTAL_RAM QUERY TOTAL_PHYSICAL_MEMORY)
if(_TOTAL_RAM GREATER 16000)
  set(VCPKG_C_FLAGS_RELEASE /GL)
  set(VCPKG_CXX_FLAGS_RELEASE /GL)
  set(VCPKG_LINKER_FLAGS_RELEASE /LTCG:INCREMENTAL)
endif()

if(PORT IN_LIST _PKG_LIBS)
  set(VCPKG_LIBRARY_LINKAGE dynamic)
endif()
