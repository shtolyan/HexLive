/*
 * hexfsb — one-sound FSB encoder over the FSBank C API (§168.6).
 *
 * The macOS FMOD Engine SDK ships libfsbank but no fsbankcl, and the browser
 * FMOD reads FSB only. This is the smallest CLI that turns loose audio into
 * one-subsound Vorbis FSBs; Tools/webgl_audio_fsb.py drives it.
 *
 *   hexfsb --quality 50 --cache <dir>  < jobs
 *
 * Each stdin line is "<source>\t<output>". One FSBank_Init for the whole
 * batch (start-up is the expensive part), then FSBank_Build per line. Every
 * line gets one answer on stdout: "ok\t<output>" or "fail\t<output>\t<why>".
 * Exit status is the number of failures, capped at 125.
 *
 * Build: Tools/webgl/build_hexfsb.sh <fmod-sdk-copy>
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "fsbank.h"

static void report_progress_failure(char *why, size_t size)
{
    const FSBANK_PROGRESSITEM *item = NULL;
    while (FSBank_FetchNextProgressItem(&item) == FSBANK_OK && item != NULL)
    {
        if (item->state == FSBANK_STATE_FAILED && item->stateData != NULL)
        {
            const FSBANK_STATEDATA_FAILED *failed = item->stateData;
            snprintf(why, size, "%s", failed->errorString);
        }
        FSBank_ReleaseProgressItem(item);
        item = NULL;
    }
}

int main(int argc, char **argv)
{
    unsigned int quality = 50;
    const char *cache = NULL;
    for (int i = 1; i + 1 < argc; i += 2)
    {
        if (strcmp(argv[i], "--quality") == 0)
        {
            quality = (unsigned int)atoi(argv[i + 1]);
        }
        else if (strcmp(argv[i], "--cache") == 0)
        {
            cache = argv[i + 1];
        }
    }
    if (cache == NULL || quality < 1 || quality > 100)
    {
        fprintf(stderr, "usage: hexfsb --quality 1..100 --cache <dir> < jobs\n");
        return 126;
    }

    FSBANK_RESULT result = FSBank_Init(FSBANK_FSBVERSION_FSB5,
                                       FSBANK_INIT_GENERATEPROGRESSITEMS, 1, cache);
    if (result != FSBANK_OK)
    {
        fprintf(stderr, "FSBank_Init failed: %d\n", (int)result);
        return 126;
    }

    int failures = 0;
    char line[8192];
    while (fgets(line, sizeof line, stdin) != NULL)
    {
        line[strcspn(line, "\r\n")] = '\0';
        char *tab = strchr(line, '\t');
        if (tab == NULL)
        {
            continue;
        }
        *tab = '\0';
        const char *source = line;
        const char *output = tab + 1;

        const char *names[1] = { source };
        FSBANK_SUBSOUND subsound;
        memset(&subsound, 0, sizeof subsound);
        subsound.fileNames = names;
        subsound.numFiles = 1;

        result = FSBank_Build(&subsound, 1, FSBANK_FORMAT_VORBIS, FSBANK_BUILD_DEFAULT,
                              quality, NULL, output);
        char why[256] = "";
        report_progress_failure(why, sizeof why);
        if (result == FSBANK_OK)
        {
            printf("ok\t%s\n", output);
        }
        else
        {
            failures++;
            printf("fail\t%s\tFSBank_Build %d %s\n", output, (int)result, why);
        }
        fflush(stdout);
    }

    FSBank_Release();
    return failures > 125 ? 125 : failures;
}
