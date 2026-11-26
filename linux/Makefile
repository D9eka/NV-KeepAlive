CC ?= gcc
CFLAGS ?= -O2 -Wall -Wextra -pthread

all: nv-keepalive

nv-keepalive: nv_keepalive.c
	$(CC) $(CFLAGS) nv_keepalive.c -ldl -o nv-keepalive

clean:
	rm -f nv-keepalive
