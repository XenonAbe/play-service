# PlayService
Run the Play Framework 2 application as a Windows service.

## Install

Run the following command as administrator. 

```
PlayService.exe /i /ServiceName=PlayServiceNameToBeSpecified ^
    /WorkDir=c:\path\to\workdir
```

##### ServiceName

The name that appears in Windows Service Manager.

##### WorkDir  

The directory where dist-zip-file and config-file are located.

```
WorkDir
├── service.conf
└── yourapp-1.0.0-SNAPSHOT.zip <- Output file for Play dist task
```

##### service.conf

This is the configuration file for the service.

example

```conf
# This is the configuration file for the service.
#
# This uses HOCON as its configuration file format.
# see: https://github.com/akkadotnet/HOCON
#
# In this file, Special environment variables can be used.
#   WorkDir: The specified work directory path
#   WorkDirName: Only the filename of the WorkDir (Path.GetFileName(WorkDir))
#   WorkDirNameUri: Uri Escaped WorkDirName
#
# For Default setting, See reference.conf file in source.

## Variables
javaHome = "/path/to/javaHome"

## Configurations for the service
service {

    ## Configurations for the target application
    app {
        # The application name
        # This name is specified in build.sbt
        name = "your-app"

        # Application options used in Play's launcher script 
        option = [
            "-Xmx1G",
        ]

        # Environment variables for execution
        environment {
            JAVA_HOME = ${javaHome}
            PATH = ${javaHome}"/bin"
            TMP = ${?WorkDir}"/tmp"
        }
    }

    # Service timeouts
    #timeoutOnStart = 1 minutes
    #timeoutOnStop = 1 minutes

    # If true, generate a test launch script that reflects the configurations when the service starts. 
    # The service does not actually start in this case
    #generateTrialLauncher = false
}
```

## Uninstall

Run the following command as administrator. 

```
PlayService.exe /u /ServiceName=YourPlayServiceName
```
