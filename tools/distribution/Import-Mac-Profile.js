ObjC.import('Foundation');
function run(args) {
    function read(path) {
        var error=Ref();
        var value=$.NSString.stringWithContentsOfFileEncodingError(path,$.NSUTF8StringEncoding,error);
        if(!value) throw Error('无法读取配置文件');
        return JSON.parse(ObjC.unwrap(value));
    }
    var profile=read(args[0]), target=args[1];
    if(!/^[0-9a-f]{32}$/.test(profile.inputPairingSecret)||profile.enableInputServer!==true) throw Error('无效的 Mac 配对文件');
    if(!/^\d{1,3}(\.\d{1,3}){3}$/.test(profile.allowedControllerAddress)||profile.allowedControllerAddress.split('.').some(function(n){return +n>255;})) throw Error('地址无效');
    var fm=$.NSFileManager.defaultManager, value={}, error=Ref();
    if(fm.fileExistsAtPath(target)) {
        value=read(target);
        if(typeof value!=='object'||Array.isArray(value)||value===null) throw Error('现有配置格式无效');
        var backup=target.replace(/settings.json$/,'settings.before-pairing-'+ObjC.unwrap($.NSUUID.UUID.UUIDString)+'.json');
        if(!fm.copyItemAtPathToPathError(target,backup,error)) throw Error('无法备份现有设置');
    }
    Object.keys(profile).forEach(function(k){
        if(!(k in value)||['enableInputServer','allowedControllerAddress','inputPairingSecret','remoteDisplayWidth','remoteDisplayHeight'].indexOf(k)>=0) value[k]=profile[k];
    });
    value.lastConnectedPeerAddress=profile.allowedControllerAddress;
    if(!$(JSON.stringify(value,null,2)).writeToFileAtomicallyEncodingError(target,true,$.NSUTF8StringEncoding,error)) throw Error('配置写入失败');
    return '配对导入成功';
}
