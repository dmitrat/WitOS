# Establish an empty directory/target before upstream target discovery.
# The deferred overlay supplies the exact NativeAOT profile and sources.
set_property(DIRECTORY PROPERTY INCLUDE_DIRECTORIES "")
set_property(DIRECTORY PROPERTY COMPILE_DEFINITIONS "")
set_property(DIRECTORY PROPERTY COMPILE_OPTIONS "")
set(CMAKE_CXX_FLAGS "")
set(CMAKE_CXX_FLAGS_RELEASE "")
add_library(WitOS.ConfigProbe STATIC)
set_target_properties(WitOS.ConfigProbe PROPERTIES EXPORT_COMPILE_COMMANDS ON ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/witos-config")
target_compile_features(WitOS.ConfigProbe PRIVATE cxx_std_17)
