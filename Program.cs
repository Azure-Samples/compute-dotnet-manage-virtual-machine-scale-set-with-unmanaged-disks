// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Samples.Common;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager;
using System.Net;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Compute;

namespace ManageVirtualMachineScaleSetWithUnmanagedDisks
{
    public class Program
    {
        private static ResourceIdentifier? _resourceGroupId = null;

        private readonly static string httpProbe = "httpProbe";
        private readonly static string httpsProbe = "httpsProbe";
        private readonly static string httpLoadBalancingRule = "httpRule";
        private readonly static string httpsLoadBalancingRule = "httpsRule";
        private readonly static string natPool50XXto22 = "natPool50XXto22";
        private readonly static string natPool60XXto23 = "natPool60XXto23";
        private readonly static string sshKey = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQCfSPC2K7LZcFKEO+/t3dzmQYtrJFZNxOsbVgOVKietqHyvmYGHEC0J2wPdAqQ/63g/hhAEFRoyehM+rbeDri4txB3YFfnOK58jqdkyXzupWqXzOrlKY4Wz9SKjjN765+dqUITjKRIaAip1Ri137szRg71WnrmdP3SphTRlCx1Bk2nXqWPsclbRDCiZeF8QOTi4JqbmJyK5+0UqhqYRduun8ylAwKKQJ1NJt85sYIHn9f1Rfr6Tq2zS0wZ7DHbZL+zB5rSlAr8QyUdg/GQD+cmSs6LvPJKL78d6hMGk84ARtFo4A79ovwX/Fj01znDQkU6nJildfkaolH2rWFG/qttD azjava@javalib.Com";
        private readonly static string apacheInstallScript = "https://raw.githubusercontent.com/Azure/azure-libraries-for-net/master/Samples/Asset/install_apache.sh";
        private readonly static string installCommand = "bash install_apache.sh Abc.123x(";

        /**
         * Azure Compute sample for managing virtual machine scale sets -
         *  - Create a virtual machine scale set behind an Internet facing load balancer
         *  - Install Apache Web servers in virtual machines in the virtual machine scale set
         *  - Stop a virtual machine scale set
         *  - Start a virtual machine scale set
         *  - Update a virtual machine scale set
         *    - Double the no. of virtual machines
         *  - Restart a virtual machine scale set
         */
        public static async Task RunSample(ArmClient client)
        {
            string rgName = Utilities.CreateRandomName("ComputeSampleRG");
            string vnetName = Utilities.CreateRandomName("vnet");
            string vmssName = Utilities.CreateRandomName("vmss");
            string storageAccountName1 = Utilities.CreateRandomName("1stg");
            string storageAccountName2 = Utilities.CreateRandomName("2stg");
            string storageAccountName3 = Utilities.CreateRandomName("3stg");
            string loadBalancerName1 = Utilities.CreateRandomName("loadbalancer");
            string publicIpName = "pip-" + loadBalancerName1;
            string frontendName = loadBalancerName1 + "-FE1";
            string backendPoolName1 = loadBalancerName1 + "-BAP1";
            string backendPoolName2 = loadBalancerName1 + "-BAP2";

            try
            {
                // Get default subscription
                SubscriptionResource subscription = await client.GetDefaultSubscriptionAsync();

                // Create a resource group in the EastUS region
                Utilities.Log($"creating resource group...");
                ArmOperation<ResourceGroupResource> rgLro = await subscription.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(AzureLocation.EastUS));
                ResourceGroupResource resourceGroup = rgLro.Value;
                _resourceGroupId = resourceGroup.Id;
                Utilities.Log("Created a resource group with name: " + resourceGroup.Data.Name);

                //=============================================================
                // Create a virtual network with a frontend subnet
                Utilities.Log("Creating virtual network with a frontend subnet ...");

                Utilities.Log("Creating virtual network...");
                VirtualNetworkData vnetInput = new VirtualNetworkData()
                {
                    Location = resourceGroup.Data.Location,
                    AddressPrefixes = { "10.10.0.0/16" },
                    Subnets =
                    {
                        new SubnetData() { Name = "Front-end", AddressPrefix = "10.10.1.0/24"},
                        new SubnetData() { Name = "subnet1", AddressPrefix = "10.10.2.0/24"},
                        new SubnetData() { Name = "subnet2", AddressPrefix = "10.10.3.0/24"},
                    },
                };
                var vnetLro = await resourceGroup.GetVirtualNetworks().CreateOrUpdateAsync(WaitUntil.Completed, vnetName, vnetInput);
                VirtualNetworkResource vnet = vnetLro.Value;
                Utilities.Log($"Created a virtual network: {vnet.Data.Name}");

                //=============================================================
                // Create a public IP address
                Utilities.Log("Creating a public IP address...");

                PublicIPAddressData publicIPInput = new PublicIPAddressData()
                {
                    Location = resourceGroup.Data.Location,
                    Sku = new PublicIPAddressSku()
                    {
                        Name = PublicIPAddressSkuName.Standard,
                        Tier = PublicIPAddressSkuTier.Regional
                    },
                    PublicIPAllocationMethod = NetworkIPAllocationMethod.Static,
                    DnsSettings = new PublicIPAddressDnsSettings { DomainNameLabel = publicIpName },
                };
                var publicIPLro = await resourceGroup.GetPublicIPAddresses().CreateOrUpdateAsync(WaitUntil.Completed, publicIpName, publicIPInput);
                PublicIPAddressResource publicIP = publicIPLro.Value;
                Utilities.Log($"Created a public IP address: {publicIPLro.Value.Data.Name}");

                //=============================================================
                // Create an Internet facing load balancer with
                // One frontend IP address
                // Two backend address pools which contain network interfaces for the virtual
                //  machines to receive HTTP and HTTPS network traffic from the load balancer
                // Two load balancing rules for HTTP and HTTPS to map public ports on the load
                //  balancer to ports in the backend address pool
                // Two probes which contain HTTP and HTTPS health probes used to check availability
                //  of virtual machines in the backend address pool
                // Three inbound NAT rules which contain rules that map a public port on the load
                //  balancer to a port for a specific virtual machine in the backend address pool
                //  - this provides direct VM connectivity for SSH to port 22 and TELNET to port 23

                Utilities.Log("Creating a Internet facing load balancer with ...");
                Utilities.Log("- A frontend IP address");
                Utilities.Log("- Two backend address pools which contain network interfaces for the virtual\n"
                        + "  machines to receive HTTP and HTTPS network traffic from the load balancer");
                Utilities.Log("- Two load balancing rules for HTTP and HTTPS to map public ports on the load\n"
                        + "  balancer to ports in the backend address pool");
                Utilities.Log("- Two probes which contain HTTP and HTTPS health probes used to check availability\n"
                        + "  of virtual machines in the backend address pool");
                Utilities.Log("- Two inbound NAT rules which contain rules that map a public port on the load\n"
                        + "  balancer to a port for a specific virtual machine in the backend address pool\n"
                        + "  - this provides direct VM connectivity for SSH to port 22 and TELNET to port 23");

                var frontendIPConfigurationId = new ResourceIdentifier($"{resourceGroup.Id}/providers/Microsoft.Network/loadBalancers/{loadBalancerName1}/frontendIPConfigurations/{frontendName}");
                var backendAddressPoolId1 = new ResourceIdentifier($"{resourceGroup.Id}/providers/Microsoft.Network/loadBalancers/{loadBalancerName1}/backendAddressPools/{backendPoolName1}");
                var backendAddressPoolId2 = new ResourceIdentifier($"{resourceGroup.Id}/providers/Microsoft.Network/loadBalancers/{loadBalancerName1}/backendAddressPools/{backendPoolName2}");
                LoadBalancerData loadBalancerInput = new LoadBalancerData()
                {
                    Location = resourceGroup.Data.Location,
                    Sku = new LoadBalancerSku()
                    {
                        Name = LoadBalancerSkuName.Standard,
                        Tier = LoadBalancerSkuTier.Regional,
                    },
                    // Explicitly define the frontend
                    FrontendIPConfigurations =
                    {
                        new FrontendIPConfigurationData()
                        {
                            Name = frontendName,
                            PrivateIPAllocationMethod = NetworkIPAllocationMethod.Dynamic,
                            PublicIPAddress = new PublicIPAddressData()
                            {
                                Id = publicIP.Id,
                                LinkedPublicIPAddress = new PublicIPAddressData()
                                {
                                    Id = publicIP.Id,
                                }
                            }
                        }
                    },
                    BackendAddressPools =
                    {
                        new BackendAddressPoolData()
                        {
                            Name = backendPoolName1
                        },
                        new BackendAddressPoolData()
                        {
                            Name = backendPoolName2
                        }
                    },
                    // Add two rules that uses above backend and probe
                    LoadBalancingRules =
                    {
                        new LoadBalancingRuleData()
                        {
                            Name = httpLoadBalancingRule,
                            FrontendIPConfigurationId = frontendIPConfigurationId,
                            BackendAddressPoolId = backendAddressPoolId1,
                            Protocol = LoadBalancingTransportProtocol.Tcp,
                            FrontendPort = 80,
                            BackendPort = 80,
                            EnableFloatingIP = false,
                            IdleTimeoutInMinutes = 15,
                            ProbeId = new ResourceIdentifier($"{resourceGroup.Id}/providers/Microsoft.Network/loadBalancers/{loadBalancerName1}/probes/{httpProbe}"),
                        },
                        new LoadBalancingRuleData()
                        {
                            Name = httpsLoadBalancingRule,
                            FrontendIPConfigurationId = frontendIPConfigurationId,
                            BackendAddressPoolId = backendAddressPoolId2,
                            Protocol = LoadBalancingTransportProtocol.Tcp,
                            FrontendPort = 443,
                            BackendPort = 443,
                            EnableFloatingIP = false,
                            IdleTimeoutInMinutes = 15,
                            ProbeId = new ResourceIdentifier($"{resourceGroup.Id}/providers/Microsoft.Network/loadBalancers/{loadBalancerName1}/probes/{httpsProbe}"),
                        },
                    },
                    // Add two probes one per rule
                    Probes =
                    {
                        new ProbeData()
                        {
                            Name = httpProbe,
                            Protocol = ProbeProtocol.Http,
                            Port = 80,
                            IntervalInSeconds = 10,
                            NumberOfProbes = 2,
                            RequestPath = "/",
                        },
                        new ProbeData()
                        {
                            Name = httpsProbe,
                            Protocol = ProbeProtocol.Https,
                            Port = 443,
                            IntervalInSeconds = 10,
                            NumberOfProbes = 2,
                            RequestPath = "/",
                        }
                    },
                    // Add two nat pools to enable direct VM connectivity for
                    //  SSH to port 22 and TELNET to port 23
                    InboundNatPools =
                    {
                        new LoadBalancerInboundNatPool()
                        {
                            Name = natPool50XXto22,
                            FrontendIPConfigurationId = frontendIPConfigurationId,
                            Protocol = LoadBalancingTransportProtocol.Tcp,
                            FrontendPortRangeStart = 5000,
                            FrontendPortRangeEnd = 5099,
                            BackendPort = 22,
                            IdleTimeoutInMinutes = 15,
                            EnableFloatingIP = false,
                        },
                        new LoadBalancerInboundNatPool()
                        {
                            Name = natPool60XXto23,
                            FrontendIPConfigurationId = frontendIPConfigurationId,
                            Protocol = LoadBalancingTransportProtocol.Tcp,
                            FrontendPortRangeStart = 6000,
                            FrontendPortRangeEnd = 6099,
                            BackendPort = 23,
                            IdleTimeoutInMinutes = 15,
                            EnableFloatingIP = false,
                        },
                    },
                };
                var loadBalancerLro1 = await resourceGroup.GetLoadBalancers().CreateOrUpdateAsync(WaitUntil.Completed, loadBalancerName1, loadBalancerInput);
                LoadBalancerResource loadBalancer1 = loadBalancerLro1.Value;

                // Print load balancer details
                Utilities.Log("Created a load balancer");

                //=============================================================
                // Create a virtual machine scale set with three virtual machines
                // And, install Apache Web servers on them

                Utilities.Log("Creating virtual machine scale set with three virtual machines"
                        + " in the frontend subnet ...");

                var t1 = DateTime.UtcNow;

                var fileUris = new List<string>();
                fileUris.Add(apacheInstallScript);

                VirtualMachineScaleSetData vmssInput = new VirtualMachineScaleSetData(resourceGroup.Data.Location)
                {
                    Sku = new()
                    {
                        Name = "Standard_DS3_v2",
                        Capacity = 3,
                        Tier = "Standard"
                    },
                    UpgradePolicy = new()
                    {
                        Mode = VirtualMachineScaleSetUpgradeMode.Manual,
                    },
                    VirtualMachineProfile = new()
                    {
                        OSProfile = new()
                        {
                            ComputerNamePrefix = vmssName,
                            AdminUsername = Utilities.CreateUsername(),
                            LinuxConfiguration = new()
                            {
                                DisablePasswordAuthentication = true,
                                SshPublicKeys =
                                {
                                    new SshPublicKeyConfiguration()
                                    {
                                        Path = $"/home/{Utilities.CreateUsername()}/.ssh/authorized_keys",
                                        KeyData = sshKey
                                    }
                                },
                            }
                        },
                        StorageProfile = new()
                        {
                            OSDisk = new VirtualMachineScaleSetOSDisk(DiskCreateOptionType.FromImage)
                            {
                                DiskSizeGB = 64,
                                Caching = CachingType.ReadWrite,
                                ManagedDisk = new VirtualMachineScaleSetManagedDisk()
                                {
                                    StorageAccountType = StorageAccountType.PremiumLrs,
                                }
                            },
                            ImageReference = new ImageReference()
                            {
                                Publisher = "Canonical",
                                Offer = "UbuntuServer",
                                Sku = "16.04-LTS",
                                Version = "latest"
                            }
                        },
                        NetworkProfile = new VirtualMachineScaleSetNetworkProfile()
                        {
                            NetworkInterfaceConfigurations =
                            {
                                new VirtualMachineScaleSetNetworkConfiguration("example")
                                {
                                    Primary = true,
                                    IPConfigurations =
                                    {
                                        new VirtualMachineScaleSetIPConfiguration("internal")
                                        {
                                            Primary = true,
                                            SubnetId = vnet.Data.Subnets.First(item=>item.Name == "Front-end").Id,
                                            LoadBalancerBackendAddressPools =
                                            {
                                                new WritableSubResource(){ Id = backendAddressPoolId1 },
                                                new WritableSubResource(){ Id = backendAddressPoolId2 }
                                            },
                                            LoadBalancerInboundNatPools =
                                            {
                                                new WritableSubResource(){ Id = loadBalancer1.Data.InboundNatPools[0].Id },
                                                new WritableSubResource(){ Id = loadBalancer1.Data.InboundNatPools[1].Id },
                                            }
                                        },
                                    },
                                }
                            }
                        }
                    }
                };

                // Use a VM extension to install Apache Web servers
                vmssInput.VirtualMachineProfile.ExtensionProfile = new VirtualMachineScaleSetExtensionProfile()
                {
                    Extensions =
                    {
                        new VirtualMachineScaleSetExtensionData("CustomScriptForLinux")
                        {

                            Publisher = "Microsoft.OSTCExtensions",
                            ExtensionType = "CustomScriptForLinux",
                            TypeHandlerVersion = "1.4",
                            AutoUpgradeMinorVersion = true,
                            EnableAutomaticUpgrade = false,
                            Settings = BinaryData.FromObjectAsJson(new
                                {
                                    fileUris = fileUris
                                }),
                            ProtectedSettings = BinaryData.FromObjectAsJson(new
                                {
                                    commandToExecute = installCommand,
                                }),
                        }
                    }
                };

                var vmssLro = await resourceGroup.GetVirtualMachineScaleSets().CreateOrUpdateAsync(WaitUntil.Completed, vmssName, vmssInput);
                VirtualMachineScaleSetResource vmss = vmssLro.Value;

                var t2 = DateTime.UtcNow;
                Utilities.Log("Created a virtual machine scale set with "
                        + "3 Linux VMs & Apache Web servers on them: (took "
                        + ((t2 - t1).TotalSeconds) + " seconds) \r\n");

                // Print virtual machine scale set details
                Utilities.Log("VMSS name: " + vmss.Data.Name);
                Utilities.Log("Capacity: " + vmss.Data.Sku.Capacity);
                Utilities.Log("VM image: " + vmss.Data.VirtualMachineProfile.StorageProfile.ImageReference.Offer);

                //=============================================================
                // Stop the virtual machine scale set

                Utilities.Log("Stopping virtual machine scale set ...");
                await vmss.PowerOffAsync(WaitUntil.Completed);
                Utilities.Log("Stopped virtual machine scale set");

                //=============================================================
                // Start the virtual machine scale set

                Utilities.Log("Starting virtual machine scale set ...");
                await vmss.PowerOnAsync(WaitUntil.Completed);
                Utilities.Log("Started virtual machine scale set");

                //=============================================================
                // Update the virtual machine scale set
                // - double the no. of virtual machines

                Utilities.Log("Updating virtual machine scale set "
                        + "- double the no. of virtual machines ...");

                VirtualMachineScaleSetData updateVmssInput = vmss.Data;
                updateVmssInput.Sku.Capacity = 6;
                _ = await resourceGroup.GetVirtualMachineScaleSets().CreateOrUpdateAsync(WaitUntil.Completed, vmssName, updateVmssInput);

                Utilities.Log("Doubled the no. of virtual machines in "
                        + "the virtual machine scale set");

                //=============================================================
                // re-start virtual machine scale set

                Utilities.Log("re-starting virtual machine scale set ...");
                await vmss.RestartAsync(WaitUntil.Completed);
                Utilities.Log("re-started virtual machine scale set");
            }
            finally
            {
                try
                {
                    if (_resourceGroupId is not null)
                    {
                        Utilities.Log($"Deleting Resource Group: {_resourceGroupId}");
                        await client.GetResourceGroupResource(_resourceGroupId).DeleteAsync(WaitUntil.Completed);
                        Utilities.Log($"Deleted Resource Group: {_resourceGroupId}");
                    }
                }
                catch (NullReferenceException)
                {
                    Utilities.Log("Did not create any resources in Azure. No clean up is necessary");
                }
                catch (Exception ex)
                {
                    Utilities.Log(ex);
                }
            }
        }

        public static async Task Main(string[] args)
        {
            try
            {
                //=================================================================
                // Authenticate
                var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
                var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
                var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
                var subscription = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                ArmClient client = new ArmClient(credential, subscription);

                await RunSample(client);
            }
            catch (Exception ex)
            {
                Utilities.Log(ex);
            }
        }
    }
}