# The try_run measurement's initial cache (plan step R1.2b), loaded after eng/native/tryrun.cmake: it forgets every
# answer that file gave, so that CMake, cross-compiling for witos, keeps each probe it compiles, lists it in
# TryRunResults.cmake and leaves it to be run on the target. The configure then stops for want of the answers.
get_cmake_property(_witos_cache CACHE_VARIABLES)
foreach(_witos_variable IN LISTS _witos_cache)
  if(_witos_variable MATCHES "_EXITCODE$" OR _witos_variable MATCHES "__TRYRUN_OUTPUT$")
    unset(${_witos_variable} CACHE)
  endif()
endforeach()
