# Kenshi Multiplayer DLL — GNU Make + MSVC
# Run from a VS Developer Command Prompt:
#   make           (Release build)
#   make DEBUG=1   (Debug build)
#   make clean

OUTDIR  := x64/Release
INTDIR  := x64/Release/obj
DLLNAME := kenshi_multiplayer.dll
TARGET  := $(OUTDIR)/$(DLLNAME)

CC   := cl
LD   := link

SOURCES := pch.cpp commands.cpp dllmain.cpp func.cpp gameState.cpp \
           gameStateGetters.cpp gameStateSetters.cpp gui_console.cpp \
           network.cpp offsets.cpp patternScan.cpp utils.cpp

OBJECTS := $(patsubst %.cpp,$(INTDIR)/%.obj,$(SOURCES))

CFLAGS := /nologo /W3 /EHsc /std:c17 /DNDEBUG /DKENSHIMULTIPLAYER_EXPORTS \
          /D_WINDOWS /D_USRDLL /DUNICODE /D_UNICODE \
          /O2 /GL /Oi /GF /Gy /MD \
          /Fp"$(INTDIR)/pch.pch"

LDFLAGS := /nologo /DLL /LTCG /OPT:REF /OPT:ICF /SUBSYSTEM:WINDOWS \
           /OUT:"$(TARGET)" /IMPLIB:"$(OUTDIR)/kenshi_multiplayer.lib"
LIBS    := kernel32.lib user32.lib gdi32.lib ws2_32.lib

ifdef DEBUG
  OUTDIR  := x64/Debug
  INTDIR  := x64/Debug/obj
  TARGET  := $(OUTDIR)/$(DLLNAME)
  OBJECTS := $(patsubst %.cpp,$(INTDIR)/%.obj,$(SOURCES))
  CFLAGS  := /nologo /W3 /EHsc /std:c17 /D_DEBUG /DKENSHIMULTIPLAYER_EXPORTS \
             /D_WINDOWS /D_USRDLL /DUNICODE /D_UNICODE \
             /Od /Zi /MDd \
             /Fp"$(INTDIR)/pch.pch" /Fd"$(INTDIR)/vc.pdb"
  LDFLAGS := /nologo /DLL /DEBUG /SUBSYSTEM:WINDOWS \
             /OUT:"$(TARGET)" /IMPLIB:"$(OUTDIR)/kenshi_multiplayer.lib"
endif

.PHONY: all clean

all: $(TARGET)

# Create output directories
$(INTDIR):
	@if not exist "$(subst /,\,$(INTDIR))" mkdir "$(subst /,\,$(INTDIR))"

# Precompiled header (must build first)
$(INTDIR)/pch.obj: pch.cpp | $(INTDIR)
	$(CC) $(CFLAGS) /Yc"pch.h" /c $< /Fo"$@"

# All other .cpp files use the PCH
$(INTDIR)/%.obj: %.cpp $(INTDIR)/pch.obj | $(INTDIR)
	$(CC) $(CFLAGS) /Yu"pch.h" /c $< /Fo"$@"

# Link
$(TARGET): $(OBJECTS)
	$(LD) $(LDFLAGS) $(OBJECTS) $(LIBS)

clean:
	@if exist "$(subst /,\,$(OUTDIR))" rd /s /q "$(subst /,\,$(OUTDIR))"
