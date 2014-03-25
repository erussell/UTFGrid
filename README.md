UTFGrid
=======

Windows Console application for generating UTFGrid files from an ArcMap .mxd

Usage: utfgrid [OPTIONS]+ mxd_document
Generate UTFGrid files from the given map document

Options:
  -d, --dir=VALUE            destination directory (defaults to current
                               directory)
  -l, --levels=VALUE         list of scale levels [0-19], separated by commas
  -f, --fields=VALUE         list of field names to include in UTFGrid data
  -t, --threads=VALUE        number of threads to use (defaults to number of
                               processors)
  -z, --zip                  zip the json files using gzip compression before
                               saving
  -o, --overwrite            overwrite existing files
  -v, --verbose              verbose output
  -h, --help                 show this message and exit
