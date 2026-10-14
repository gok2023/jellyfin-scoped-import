"""Enable TMDb only on the isolated Lab Other fixture, restore options afterwards."""
import copy,json,time,uuid
from support import lab
from support.lab import connect

def run():
    connect()
    folder=next(x for x in lab.require('GET','/Library/VirtualFolders') if x['Name']=='Lab Other')
    original=folder['LibraryOptions']; options=copy.deepcopy(original)
    options['EnableInternetProviders']=True
    options['PreferredMetadataLanguage']='en'
    for t in options['TypeOptions']:
        if t['Type']=='Movie':t['MetadataFetchers']=['TheMovieDb']
    evidence={'provider':'built-in TheMovieDb','production_accessed':False}
    try:
        lab.require('POST','/Library/VirtualFolders/LibraryOptions',{'Id':folder['ItemId'],'LibraryOptions':options},204)
        movie=lab.video('other/Remote Metadata '+uuid.uuid4().hex[:8]+'/The.Matrix.1999.mp4')
        movie.with_suffix('.nfo').write_text('<movie><uniqueid type="tmdb">603</uniqueid></movie>',encoding='utf-8')
        reply=lab.require('POST','/ScopedImport/Import',{'Path':str(movie.parent),'FetchMetadata':True},202)
        for _ in range(100):
            result=lab.require('GET','/ScopedImport/Operations/'+reply['OperationId'])
            if result['State'] in ('Completed','Rejected','Partial'):break
            time.sleep(.5)
        evidence['operation']=result
        assert result['State']=='Completed' and len(result['Created'])==1,result
        item=next(x for x in lab.snapshot()['Items'] if x['Id']==result['Created'][0]['Id'])
        evidence['item']=item
        evidence['remote_metadata_verified']=bool(item.get('Overview')) and item.get('ProductionYear')==1999 and item['Name']=='The Matrix'
        assert evidence['remote_metadata_verified'], 'Expected TMDb metadata was not returned'
        print('TMDb remote metadata verified:',evidence['remote_metadata_verified'])
    finally:
        lab.require('POST','/Library/VirtualFolders/LibraryOptions',{'Id':folder['ItemId'],'LibraryOptions':original},204)
        evidence['library_options_restored']=True
        (lab.ROOT/'evidence/remote-metadata.json').write_text(json.dumps(evidence,ensure_ascii=False,indent=2),encoding='utf-8')

if __name__=='__main__':run()
