# HomeYarp
local config and code for yarp at home


## when running container use these parameters
Adjust volume with settings to wherever json config files are. Logs are going to be saved there too.
```
<DockerfileRunArguments>-p 5555:5555 -v C:\temp\volume:/app/config</DockerfileRunArguments>
```

## Config files saved in the config volume:
- ReverseProxy.json
- Auth.json

### ReverseProxy.json config file:
This is the config file for YARP. this is a simple example:
```
{
  "ReverseProxy": {
    "Routes": {
      "route1": {
        "ClusterId": "cluster1",
        "Match": {
          "Path": "{**catch-all}",
          "Hosts": [ "localhost" ]
        }
      }
    },
    "Clusters": {
      "cluster1": {
        "Destinations": {
          "cluster1/destination1": {
            "Address": "https://google.com/"
          }
        }
      }
    }
  }
}
```

### Authentication / authorization
Use Auth.json to save your authorization provider details. These should never be shared.