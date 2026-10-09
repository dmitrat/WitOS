# CMake's system WitOS (plan step R1.2b): the system layer's programs and libraries are ELF images of the layer-2
# triples, built by clang and lld as Linux musl ones are, so CMake's compiler rules are Linux's. WitOS is not Linux:
# CMAKE_SYSTEM_NAME stays WitOS and LINUX stays unset, so that a project tells the two apart.
set(UNIX 1)
set(CMAKE_EFFECTIVE_SYSTEM_NAME "Linux")
