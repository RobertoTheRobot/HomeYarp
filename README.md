# HomeYarp
local config and code for yarp at home


## when running container use these parameters
Adjust volume with settings to wherever AppSettings.json and ReverseProxy.json files are
```
<DockerfileRunArguments>-p 5555:5555 -v C:\temp\volume:/app/config</DockerfileRunArguments>
```